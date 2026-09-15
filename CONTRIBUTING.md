# Участие в разработке

Спасибо за интерес к GrpcEmbed.

## Локальная проверка

```powershell
dotnet restore GrpcEmbed.sln
dotnet build GrpcEmbed.sln -c Release -m:1 /nodeReuse:false
dotnet test tests\GrpcEmbed.Tests\GrpcEmbed.Tests.csproj -c Release
```

Изменения публичных контрактов должны сопровождаться тестами совместимости схемы. Изменения transport hot path должны сохранять стандартную gRPC-совместимость и отсутствие JSON.

Перед pull request убедитесь, что сборка проходит без предупреждений и ошибок.
