# Interface Generation for HaloInfiniteClient

This document explains how the `IHaloInfiniteClient` interface is automatically generated from the `HaloInfiniteClient` class using a C# Source Generator.

## Overview

The `IHaloInfiniteClient` interface is automatically generated using a proper C# Source Generator that implements the `IIncrementalGenerator` interface. This ensures that:

1. The interface always stays in sync with the class implementation
2. Any changes to public methods are automatically reflected in the interface during compilation
3. The interface provides a clean abstraction for dependency injection and testing
4. No manual regeneration is required - the interface is generated at compile time

## How it Works

### GenerateInterface Attribute

The `HaloInfiniteClient` class is marked with the `[GenerateInterface]` attribute:

```csharp
[GenerateInterface]
public class HaloInfiniteClient : IHaloInfiniteClient
{
    // Class implementation...
}
```

This attribute indicates that an interface should be generated for this class.

### C# Source Generator

The interface is generated using a proper C# Source Generator (`Grunt.SourceGenerator`) that:

1. Implements `IIncrementalGenerator` with the `[Generator]` attribute
2. Automatically detects classes marked with `[GenerateInterface]` during compilation
3. Extracts all public properties and methods
4. Generates the corresponding interface declarations at compile time
5. Creates the `IHaloInfiniteClient.generated.cs` file in memory during build

### Generated Interface Structure

The generated interface includes:

- **Properties**: `SpartanToken`, `Xuid`, `ClearanceToken`
- **Methods**: All public async methods that return `Task<HaloApiResultContainer<T, HaloApiErrorContainer>>`

Example generated interface members:

```csharp
public interface IHaloInfiniteClient
{
    string SpartanToken { get; set; }
    string Xuid { get; set; }
    string ClearanceToken { get; set; }
    
    Task<HaloApiResultContainer<ApiSettingsContainer, HaloApiErrorContainer>> GetApiSettingsContainer();
    Task<HaloApiResultContainer<BotCustomizationData, HaloApiErrorContainer>> AcademyGetBotCustomization(string flightId);
    // ... all other public methods including generic methods
}
```

## Generated factory and request policy

The same generator emits the generated half of `partial class HaloInfiniteClientFactory : IHaloInfiniteClient`. Each method is a one-liner:

```csharp
public Task<HaloApiResultContainer<MatchStats, HaloApiErrorContainer>> StatsGetMatchStats(string matchId)
    => ExecuteWithPolicyAsync("StatsGetMatchStats", client => client.StatsGetMatchStats(matchId), static response => response?.Error);
```

`ExecuteWithPolicyAsync` (hand-written half) runs the call under `HaloRequestPolicy`:

- **401/403**: rebuild the client with fresh tokens and retry once (same as before). This doesn't count against the retry budget.
- **429, 408, 500, 502, 503, 504 and network errors** (`HttpRequestException`, timeouts): retry with exponential backoff and equal jitter, honouring `Retry-After` (captured on `HaloApiErrorContainer.RetryAfter`).

Configure it under `ClientConfiguration:Retry`:

| Setting | Default |
|:--------|:--------|
| `MaxAttempts` | 4 (1 turns transient retries off) |
| `BaseDelay` | 500 ms |
| `MaxDelay` | 20 s |
| `MaxRetryAfter` | 60 s (a longer `Retry-After` is returned instead of waited out) |
| `AttemptTimeout` | none (when set, an attempt unanswered this long is retried as a timeout; never the last attempt) |
| `UseJitter` | true |
| `RetryOnNetworkErrors` | true |
| `RetryStatusCodes` | the defaults listed above |

Retries are counted in the `grunt.halo.retries` metric (meter `Surprenant.Grunt`).

## Gateway generation

`GatewayGenerator` (same analyzer assembly) turns `[GatewayExposed]` methods into an ASP.NET Core controller and an HTTP client. See [GATEWAY.md](GATEWAY.md).

## Compilation-Time Generation

The source generator runs automatically during each build:

```bash
dotnet build
```

The interface is generated in memory during compilation and does not create physical files in the source directory. This follows the standard pattern for C# Source Generators.

## Benefits

1. **Automatic**: No manual intervention required - runs during every build
2. **Consistency**: The interface is always consistent with the class
3. **Maintainability**: No need to manually update interface when methods change
4. **Type Safety**: Compile-time verification that the class implements all interface members
5. **Testing**: Easy to create mock implementations for unit testing
6. **Dependency Injection**: Clean interface for IoC container registration
7. **Industry Standard**: Uses proper C# Source Generator infrastructure

## Usage Example

```csharp
// Register in dependency injection container
services.AddScoped<IHaloInfiniteClient, HaloInfiniteClient>();

// Use through interface
public class MyService
{
    private readonly IHaloInfiniteClient _client;
    
    public MyService(IHaloInfiniteClient client)
    {
        _client = client;
    }
    
    public async Task<ApiSettingsContainer> GetSettings()
    {
        var result = await _client.GetApiSettingsContainer();
        return result.Result;
    }
}
```

## Files

- `Grunt/Grunt/Core/HaloInfiniteClient.cs` - The implementation class (marked with `[GenerateInterface]`)
- `Grunt/Grunt/Attributes/GenerateInterfaceAttribute.cs` - The attribute used to mark classes for interface generation
- `Grunt/Grunt.SourceGenerator/` - The C# Source Generator project with `IIncrementalGenerator` implementation
- Generated `IHaloInfiniteClient.generated.cs` - The auto-generated interface (exists in memory during compilation)

## Technical Details

The source generator:
- Uses `IIncrementalGenerator` for optimal performance
- Marked with `[Generator]` attribute for proper registration
- Targets `netstandard2.0` for maximum compatibility
- Automatically handles most method signatures including async methods
- Generates interfaces with proper using statements and namespaces

## Note

The generated interface file exists only in memory during compilation and is not written to disk. This follows the standard pattern for C# Source Generators. All changes should be made to the `HaloInfiniteClient` class, and the interface will be automatically regenerated during the next build.