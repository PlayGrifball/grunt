using Surprenant.Grunt.Attributes;
using Surprenant.Grunt.Core;

// Tells Grunt.SourceGenerator.GatewayGenerator to emit HaloGatewayClient (namespace Surprenant.Grunt.Gateway.Client) implementing
// IHaloInfiniteClient: [GatewayExposed] methods of HaloInfiniteClient call the gateway, the rest throw NotSupportedException.
[assembly: GenerateGatewayClient(typeof(HaloInfiniteClient))]
