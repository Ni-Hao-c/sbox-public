var host = await Sandbox.Mounting.Directory.Mount( "titanfall2" );
var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
var loaderType = host.GetType().Assembly.GetType( "ModelLoader" );
var usable = loaderType.GetMethod( "IsUsableModel", flags );
bool IsUsable( Model model ) => (bool)usable.Invoke( null, new object[] { model } );
var path = "mount://titanfall2/models/industrial/hanging_plant_mister.mdl.t2static.all.vmdl";
var errorWrapper = Model.Load( path );
var scene = new Scene();
try
{
	var component = new GameObject( scene, false, "Model failure probe" ).AddComponent<Titanfall2StaticPropStreamer>();
	var instanceFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
	var load = component.GetType().GetMethod( "LoadModelWithRetryCache", instanceFlags );
	var retries = (System.Collections.IDictionary)component.GetType().GetField( "_modelFailureRetryAfter", instanceFlags ).GetValue( component );
	var first = load.Invoke( component, new object[] { path } );
	var deadline = retries[path];
	var second = load.Invoke( component, new object[] { path } );
	return new {
		validAccepted = IsUsable( Model.Cube ), singletonRejected = !IsUsable( Model.Error ),
		wrapperIsError = errorWrapper?.IsError, wrapperIsSingleton = errorWrapper == Model.Error,
		wrapperRejected = !IsUsable( errorWrapper ),
		retryCached = first is null && second is null && deadline is not null && deadline.Equals( retries[path] ),
		passed = IsUsable( Model.Cube ) && !IsUsable( Model.Error ) && !IsUsable( errorWrapper )
			&& first is null && second is null && deadline is not null && deadline.Equals( retries[path] ) };
}
finally { scene.Destroy(); }
