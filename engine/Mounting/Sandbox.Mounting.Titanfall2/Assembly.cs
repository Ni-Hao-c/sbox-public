global using Sandbox;
global using Sandbox.Mounting;
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading.Tasks;

// Map import sharing, scene-resource leases and world build disposal are internal mechanics
// with no public surface, so their regression tests need access.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo( "Sandbox.Test.Engine" )]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo( "Sandbox.Test.Integration" )]
