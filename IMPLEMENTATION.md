# Итоговый отчёт о реализации GrpcEmbed

## Реализовано

- Поиск MVC-действий во время запуска через `IActionDescriptorCollectionProvider`.
- Режимы экспорта всех совместимых действий и только явно отмеченных действий.
- Поддержка `GrpcExport`, `GrpcIgnore`, `GrpcName` и пользовательского предиката фильтрации.
- Настоящие unary gRPC-методы, зарегистрированные через `IServiceMethodProvider<TService>` и обслуживаемые `Grpc.AspNetCore`.
- Однократная генерация CLR-типов запросов через Reflection.Emit и прямая protobuf-сериализация без JSON.
- Стабильные номера protobuf-полей, не зависящие от порядка reflection.
- Явные номера `[GrpcFieldNumber]`, зарезервированные номера `[GrpcReservedField]` и безопасное переименование полей.
- Детерминированные `.proto`, descriptor set и SHA-256 хеш схемы.
- Стандартный gRPC Server Reflection v1alpha.
- Активация и освобождение MVC-контроллеров через `IControllerFactory`.
- Scoped DI, `HttpContext`, `HttpRequest`, `HttpResponse`, `ClaimsPrincipal`, сервисные параметры и `CancellationToken`.
- Authorization-, resource-, action-, result- и exception-фильтры MVC.
- Проверка DataAnnotations для параметров и DTO.
- Преобразование `ActionResult<T>`, `ObjectResult` и HTTP-статусов в ответы и статусы gRPC.
- Контрактный клиент на обычных C#-интерфейсах через `DispatchProxy` и `Grpc.Net.Client`.
- Поддержка `Task<T>` и `ValueTask<T>`, DTO, скаляров, строк, массивов байтов и коллекций.
- `GrpcEmbedTransportMode.GrpcOnly`, deadline, metadata, cancellation и проверка ожидаемого schema hash.
- Отдельный переиспользуемый канал для каждого клиентского контракта с корректным освобождением через DI.
- Кэширование при запуске schema hash, request readers, планов аргументов, validation attributes, response wrappers, клиентских фабрик запросов и readers ответов.
- Сохранение binding source и field number в discovery context и schema manifest.
- Диагностические/schema endpoints отключены по умолчанию и могут быть защищены authorization policy.
- Стандартная совместимость с внешним generated gRPC client.
- Integration-тест, подтверждающий отсутствие вызовов System.Text.Json в native gRPC-пути.
- Проверка совместимости manifest для полей, сервисов, методов и типов запросов/ответов.
- Поддержка схем enum в `.proto` и descriptor set.
- Runtime round-trip для примитивов, nullable, коллекций, enum, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `decimal`, `byte[]` и вложенных DTO.
- Реалистичные sample-контроллеры Users, Orders, Payments, Files и Admin.
- NuGet-пакеты `DevelKit.GrpcEmbed*` с MIT, README, repository metadata, symbols и deterministic build.
- Независимый net8.0 consumer успешно собирается только из локальных NuGet-пакетов.

## Архитектура

### Сервер

1. При запуске MVC предоставляет набор `ControllerActionDescriptor`.
2. Политика экспорта исключает несовместимые и отмеченные `GrpcIgnore` действия.
3. Для параметров действий один раз создаются protobuf request-типы.
4. DTO добавляются в общий `RuntimeTypeModel` protobuf-net.
5. Заранее компилируются делегаты чтения параметров, вызова action и упаковки ответа.
6. `Grpc.AspNetCore` регистрирует стандартные gRPC routes вида `/GrpcEmbed.Users/Get`.
7. В запросе protobuf сразу десериализуется в CLR-объект, после чего вызывается существующий MVC action.
8. Результат MVC преобразуется в protobuf-ответ без промежуточной JSON-сериализации.

### Клиент

1. DI анализирует обычный интерфейс контракта при первом разрешении.
2. Один раз строятся request-типы, marshallers и делегаты создания запросов.
3. Для контракта создаётся и переиспользуется собственный `GrpcChannel`.
4. Вызов интерфейса выполняет стандартный unary gRPC request через `CallInvoker`.
5. Ответ protobuf непосредственно преобразуется в ожидаемый CLR-тип.

### Схема и совместимость

- `.proto`, descriptor set, manifest и SHA-256 schema hash создаются детерминированно.
- В manifest входят сервисы, методы, типы, поля, binding sources и номера полей.
- Клиент может проверять `grpcembed-schema-hash` из response headers.
- Strict startup validation обнаруживает удаление и переименование контрактов, изменение типов и небезопасное переназначение field number.

## Осознанно не поддерживается в версии 1

- Пользовательские MVC model binders, value providers и formatter-specific results не выполняются в protobuf-пути. Binding metadata сохраняется, но аргументы уже приходят типизированными из protobuf-запроса.
- Streaming предусмотрен в `GrpcEmbedMethodType`, однако первая версия реализует только unary RPC, как установлено границами MVP в ТЗ.
- Reflection.Emit и DispatchProxy несовместимы с NativeAOT и безопасным trimming. Публичные точки входа помечены `RequiresDynamicCode` и `RequiresUnreferencedCode` там, где это поддерживается target framework.
- Файловые, потоковые, multipart и другие транспортно-зависимые MVC-результаты не экспортируются.
- Неоднозначные gRPC-имена, CLR overloads, неподдерживаемые async return types и некорректное расположение `CancellationToken` отклоняются при создании клиента.

## Производительность

BenchmarkDotNet ShortRun выполнен на .NET 10.0.11 и Intel Core i5-12500H через ASP.NET Core TestServer.

Значения в таблице: средняя задержка / выделенная управляемая память на операцию.

| Размер данных | MVC + JSON | Ручной native gRPC | GrpcEmbed + generated client | GrpcEmbed + contract proxy |
|---:|---:|---:|---:|---:|
| 32 Б | 119,57 мкс / 17,37 КБ | 35,81 мкс / 14,64 КБ | 85,19 мкс / 22,14 КБ | 83,87 мкс / 23,42 КБ |
| 200 Б | 98,22 мкс / 18,19 КБ | 28,11 мкс / 15,29 КБ | 88,45 мкс / 22,80 КБ | 102,90 мкс / 24,05 КБ |
| 2 КБ | 153,78 мкс / 27,45 КБ | 35,35 мкс / 22,51 КБ | 96,09 мкс / 36,02 КБ | 85,75 мкс / 43,29 КБ |
| 20 КБ | 224,11 мкс / 138,32 КБ | 88,64 мкс / 116,29 КБ | 121,68 мкс / 165,85 КБ | 116,09 мкс / 229,19 КБ |

Это короткий in-memory прогон с широкими доверительными интервалами, а не измерение сетевой пропускной способности production-системы. Текущая производительность GrpcEmbed составляет примерно 42–76% от ручного native gRPC в зависимости от размера данных и клиента. Целевые 85–95% пока не достигнуты и остаются честной точкой дальнейшей оптимизации.

## Совместимость с платформами

| Платформа | Сборка библиотек | Runtime-проверка Kestrel |
|---|---|---|
| .NET 6 | успешно | REST и HTTP/2 gRPC успешно |
| .NET 7 | успешно | ASP.NET runtime отсутствует на проверочном компьютере |
| .NET 8 | успешно | REST и HTTP/2 gRPC успешно |
| .NET 9 | успешно | ASP.NET runtime отсутствует на проверочном компьютере |
| .NET 10 | успешно | REST и HTTP/2 gRPC успешно |

Итоговая сборка завершена без ошибок и предупреждений. Тесты: 20 из 20 успешно.

## NuGet-пакеты

- `DevelKit.GrpcEmbed`
- `DevelKit.GrpcEmbed.AspNetCore`
- `DevelKit.GrpcEmbed.Client`
- `DevelKit.GrpcEmbed.Core`
- `DevelKit.GrpcEmbed.Abstractions`

Для каждого пакета сформированы `.nupkg` и `.snupkg`. Старые артефакты с идентификаторами `GrpcEmbed.*` удалены.

В package metadata указан фактический репозиторий: `https://github.com/develmax/DevelKit.GrpcEmbed`.

## Команды

Восстановление зависимостей:

```powershell
dotnet restore GrpcEmbed.sln
```

Release-сборка:

```powershell
dotnet build GrpcEmbed.sln -c Release -m:1 /nodeReuse:false
```

Тесты:

```powershell
dotnet test tests\GrpcEmbed.Tests\GrpcEmbed.Tests.csproj -c Release
```

Sample server:

```powershell
dotnet run --project samples\GrpcEmbed.Sample.Server -c Release -f net10.0
```

REST работает на `http://localhost:5000`, gRPC/HTTP2 — на `http://localhost:5001`.

Sample client:

```powershell
dotnet run --project samples\GrpcEmbed.Sample.Client -c Release -- http://localhost:5001
```

Полный транспортный benchmark:

```powershell
dotnet run --project benchmarks\GrpcEmbed.Benchmarks -c Release -- --filter *PayloadTransportBenchmarks*
```

Создание NuGet-пакета верхнего уровня:

```powershell
dotnet pack src\GrpcEmbed\GrpcEmbed.csproj -c Release -o artifacts\packages
```

Экспорт схемы после запуска sample server:

```powershell
curl.exe http://localhost:5000/_grpcembed/schema.proto
curl.exe http://localhost:5000/_grpcembed/descriptor.pb --output grpcembed.protoset
curl.exe http://localhost:5000/_grpcembed/schema.json
```

## Итог

GrpcEmbed реализован как работающий interoperable unary gRPC-транспорт для существующих ASP.NET Core MVC API. Контроллеры и DTO не требуется переписывать, GrpcEmbed-клиент использует обычные C#-интерфейсы, а внешние стандартные gRPC-клиенты могут работать с экспортированной схемой.
