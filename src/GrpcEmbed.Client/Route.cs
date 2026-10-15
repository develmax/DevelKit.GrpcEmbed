using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GrpcEmbed.Client;

public sealed class GrpcEmbedClientRoutingOptions
{
    public GrpcEmbedRoutingMode Mode { get; set; } = GrpcEmbedRoutingMode.Native;
    public string Prefix { get; set; } = "grpc";
    /// <summary>Optional application mount path, without the API route. Otherwise inferred from Address.</summary>
    public string? PathBase { get; set; }
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ClientRoute
{
    private static readonly Regex Tokens = new(@"\{([^{}]+)\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly string _template;
    private readonly Dictionary<string, string> _parameters;
    private readonly string[] _segments;
    private readonly Regex[] _prefixes;

    private ClientRoute(string template, Dictionary<string, string> parameters)
    {
        _template = template.TrimStart('~', '/');
        _parameters = parameters;
        _segments = _template.Split('/');
        _prefixes = new Regex[_segments.Length];
        // Compile segment matchers once per contract snapshot, never per request.
        for (var i = 0; i < _segments.Length; i++)
        {
            var segment = _segments[i];
            var position = 0;
            var pattern = "^";
            foreach (Match token in Tokens.Matches(segment))
            {
                pattern += Regex.Escape(segment[position..token.Index]) + "([^/]+)";
                position = token.Index + token.Length;
            }
            pattern += Regex.Escape(segment[position..]) + "$";
            _prefixes[i] = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
    }

    public static ClientRoute? Parse(JsonElement json)
    {
        if (!json.TryGetProperty("template", out var template) || template.ValueKind != JsonValueKind.String) return null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (json.TryGetProperty("parameters", out var bindings))
            foreach (var binding in bindings.EnumerateObject()) parameters.Add(binding.Name, binding.Value.GetString()!);
        return new ClientRoute(template.GetString()!, parameters);
    }

    private static string Name(string token) => token.TrimStart('*').Split(':', '=', '?')[0];

    // Bind configuration, route-prefix inference and parameter names once. Only
    // argument values and their escaping belong on the per-RPC hot path.
    public static Func<object?[], string> Bind(GrpcEmbedClientOptions options, ClientRoute? route,
        string service, string method, ParameterInfo[] parameters)
    {
        if (options.Routing.Mode != GrpcEmbedRoutingMode.Rest)
        {
            var path = Build(options, route, service, method, parameters, Array.Empty<object?>());
            return _ => path;
        }
        return (route ?? throw new InvalidOperationException("The contract does not publish a REST route."))
            .BindRest(options, parameters);
    }

    private Func<object?[], string> BindRest(GrpcEmbedClientOptions options, ParameterInfo[] parameters)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.Routing.Values) values[pair.Key] = pair.Value;
        var mount = options.Routing.PathBase;
        if (mount is null)
        {
            var baseSegments = options.Address.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            mount = options.Address.AbsolutePath.TrimEnd('/');
            for (var start = 0; start < baseSegments.Length; start++)
            {
                var count = baseSegments.Length - start;
                if (count > _segments.Length) continue;
                var matches = Enumerable.Range(0, count).Select(i => _prefixes[i].Match(Uri.UnescapeDataString(baseSegments[start + i]))).ToArray();
                if (matches.Any(match => !match.Success)) continue;
                mount = string.Join("/", baseSegments.Take(start));
                for (var i = 0; i < count; i++)
                {
                    var group = 1;
                    foreach (Match token in Tokens.Matches(_segments[i]))
                    {
                        var name = Name(token.Groups[1].Value);
                        if (!values.ContainsKey(name)) values[name] = matches[i].Groups[group].Value;
                        group++;
                    }
                }
                break;
            }
        }
        var arguments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parameters.Length; i++)
        {
            var name = parameters[i].Name!;
            arguments[_parameters.FirstOrDefault(pair => pair.Value == name).Key ?? name] = i;
        }
        var bindings = Tokens.Matches(_template).Cast<Match>().Select(token =>
        {
            var spec = token.Groups[1].Value;
            var name = Name(spec);
            values.TryGetValue(name, out var configured);
            var fallback = spec.Contains('=') ? spec[(spec.IndexOf('=') + 1)..].TrimEnd('?') : null;
            return (token.Index, token.Length, Name: name, Argument: arguments.TryGetValue(name, out var index) ? index : -1,
                Configured: configured, Fallback: fallback, Optional: spec.EndsWith('?'), CatchAll: spec.StartsWith("**", StringComparison.Ordinal));
        }).ToArray();
        var prefix = "/" + (mount.Trim('/').Length == 0 ? "" : mount.Trim('/') + "/");
        Func<object?[], string> render = args =>
        {
            var path = new System.Text.StringBuilder(_template.Length + 32);
            var position = 0;
            foreach (var binding in bindings)
            {
                path.Append(_template, position, binding.Index - position);
                var value = binding.Argument >= 0 ? Convert.ToString(args[binding.Argument], CultureInfo.InvariantCulture) : binding.Configured;
                // Convert.ToString(null) returns empty, while a null route value
                // must still select the default/optional/missing-value behavior.
                if (binding.Argument >= 0 && args[binding.Argument] is null) value = null;
                value ??= binding.Fallback;
                if (value is null && !binding.Optional)
                    throw new InvalidOperationException($"No value for route parameter '{binding.Name}'. Configure Grpc:Routing:Values:{binding.Name}.");
                if (value is not null)
                {
                    if (value is "." or ".." || value.Contains('/') && value.Split('/').Any(segment => segment is "." or ".."))
                        throw new InvalidOperationException("Dot path segments are not supported.");
                    path.Append(binding.CatchAll ? string.Join("/", value.Split('/').Select(Uri.EscapeDataString)) : Uri.EscapeDataString(value));
                }
                position = binding.Index + binding.Length;
            }
            path.Append(_template, position, _template.Length - position);
            var result = path.ToString().Trim('/');
            if (result.Contains('{') || result.Contains('}')) throw new NotSupportedException("Unsupported route template.");
            return prefix + result;
        };
        // Most GET routes have one varying scalar (for example pfpId), while
        // version/mount are fixed by the loaded contract. Compile those literal
        // parts once rather than allocating a builder and an intermediate path
        // for every RPC. Retain the general renderer for all corner cases.
        var dynamicBindings = bindings.Where(binding => binding.Argument >= 0).ToArray();
        if (dynamicBindings.Length != 1 || dynamicBindings[0].Optional || dynamicBindings[0].CatchAll ||
            !RequestSerialization.IsScalar(parameters[dynamicBindings[0].Argument].ParameterType)) return render;
        var left = new System.Text.StringBuilder(prefix);
        var right = new System.Text.StringBuilder();
        var destination = left;
        var offset = 0;
        foreach (var binding in bindings)
        {
            var literal = _template.AsSpan(offset, binding.Index - offset);
            if (literal.Contains('{') || literal.Contains('}')) return render;
            destination.Append(literal);
            if (binding.Argument >= 0) destination = right;
            else
            {
                var value = binding.Configured ?? binding.Fallback;
                if (string.IsNullOrEmpty(value) || binding.CatchAll || value is "." or ".." ||
                    value.Contains('/') && value.Split('/').Any(segment => segment is "." or "..")) return render;
                destination.Append(Uri.EscapeDataString(value));
            }
            offset = binding.Index + binding.Length;
        }
        var tail = _template.AsSpan(offset);
        if (tail.Contains('{') || tail.Contains('}')) return render;
        right.Append(tail);
        var before = left.ToString();
        var after = right.ToString().TrimEnd('/');
        var dynamicBinding = dynamicBindings[0];
        return args =>
        {
            var value = args[dynamicBinding.Argument] is null ? dynamicBinding.Fallback
                : Convert.ToString(args[dynamicBinding.Argument], CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(value)) return render(args);
            if (value is "." or ".." || value.Contains('/') && value.Split('/').Any(segment => segment is "." or ".."))
                throw new InvalidOperationException("Dot path segments are not supported.");
            return string.Concat(before, Uri.EscapeDataString(value), after);
        };
    }

    public static string Build(GrpcEmbedClientOptions options, ClientRoute? route, string service, string method, ParameterInfo[] parameters, object?[] args)
    {
        if (options.Routing.Mode == GrpcEmbedRoutingMode.Rest)
            return (route ?? throw new InvalidOperationException("The contract does not publish a REST route.")).BuildRest(options, parameters, args);
        var prefix = options.Routing.PathBase ?? options.Address.AbsolutePath;
        return "/" + string.Join("/", new[] { prefix.Trim('/'), options.Routing.Prefix.Trim('/'),
            options.Routing.Mode == GrpcEmbedRoutingMode.ControllerMethod ? service.Replace("GrpcEmbed.", "") : "", method }.Where(value => value.Length > 0));
    }

    private string BuildRest(GrpcEmbedClientOptions options, ParameterInfo[] parameters, object?[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.Routing.Values) values[pair.Key] = pair.Value;
        var baseSegments = options.Address.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var mount = options.Routing.PathBase;
        if (mount is null)
        {
            mount = options.Address.AbsolutePath.TrimEnd('/');
            // The REST BaseAddress may already contain the first API route segments.
            for (var start = 0; start < baseSegments.Length; start++)
            {
                var count = baseSegments.Length - start;
                if (count > _segments.Length) continue;
                var matches = Enumerable.Range(0, count).Select(i => _prefixes[i].Match(Uri.UnescapeDataString(baseSegments[start + i]))).ToArray();
                if (matches.Any(match => !match.Success)) continue;
                mount = string.Join("/", baseSegments.Take(start));
                for (var i = 0; i < count; i++)
                {
                    var group = 1;
                    foreach (Match token in Tokens.Matches(_segments[i]))
                    {
                        var name = Name(token.Groups[1].Value);
                        if (!values.ContainsKey(name)) values[name] = matches[i].Groups[group].Value;
                        group++;
                    }
                }
                break;
            }
        }
        for (var i = 0; i < parameters.Length; i++)
        {
            var name = parameters[i].Name!;
            var routeName = _parameters.FirstOrDefault(pair => pair.Value == name).Key ?? name;
            values[routeName] = args[i] is null ? null : Convert.ToString(args[i], CultureInfo.InvariantCulture);
        }
        var path = Tokens.Replace(_template, token =>
        {
            var spec = token.Groups[1].Value;
            var name = Name(spec);
            values.TryGetValue(name, out var value);
            if (value is null && spec.Contains('=')) value = spec[(spec.IndexOf('=') + 1)..].TrimEnd('?');
            if (value is null && spec.EndsWith('?')) return "";
            if (value is null) throw new InvalidOperationException($"No value for route parameter '{name}'. Configure Grpc:Routing:Values:{name}.");
            if (value.Split('/').Any(segment => segment is "." or "..")) throw new InvalidOperationException("Dot path segments are not supported.");
            return spec.StartsWith("**", StringComparison.Ordinal) ? string.Join("/", value.Split('/').Select(Uri.EscapeDataString)) : Uri.EscapeDataString(value);
        }).Trim('/');
        if (path.Contains('{') || path.Contains('}')) throw new NotSupportedException("Unsupported route template.");
        return "/" + (mount.Trim('/').Length == 0 ? "" : mount.Trim('/') + "/") + path;
    }
}
