using Surprenant.Grunt.Attributes;
using Surprenant.Grunt.Core;

// Tells Grunt.SourceGenerator.GatewayGenerator to emit HaloInfiniteController (namespace Surprenant.Grunt.Gateway) with one action
// per [GatewayExposed] method of HaloInfiniteClient. The client type lives in the referenced Surprenant.Grunt assembly; the
// generator reads its methods and attributes from metadata.
[assembly: GenerateGatewayController(typeof(HaloInfiniteClient))]
