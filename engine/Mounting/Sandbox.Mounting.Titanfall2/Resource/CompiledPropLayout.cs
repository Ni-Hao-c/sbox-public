/// <summary>
/// Engine-object-free layout of a static scene. Both permutations refer to dense instance IDs,
/// never BSP source indices (which may be missing or sparse). Compiled at import, read at load.
/// </summary>
internal sealed class Titanfall2CompiledPropLayout
{
	const int Magic = 0x314C4354; // TCL1
	internal const float CellSize = 512f;
	internal readonly record struct Instance( Vector3 Position, int ModelIndex, int Skin );
	internal readonly record struct Cell( int X, int Y, int First, int Count );
	internal readonly record struct ModelGroup( int ModelIndex, int Skin, int First, int Count );
	internal Cell[] Cells { get; }
	internal int[] CellInstances { get; }
	internal ModelGroup[] Models { get; }
	internal int[] ModelInstances { get; }
	internal int InstanceCount => CellInstances.Length;

	Titanfall2CompiledPropLayout( Cell[] cells, int[] cellInstances, ModelGroup[] models, int[] modelInstances )
	{
		Cells = cells;
		CellInstances = cellInstances;
		Models = models;
		ModelInstances = modelInstances;
	}

	internal static Titanfall2CompiledPropLayout Compile( IReadOnlyList<Instance> instances )
	{
		ArgumentNullException.ThrowIfNull( instances );
		var cells = new Dictionary<(int X, int Y), List<int>>();
		var models = new Dictionary<(int Model, int Skin), List<int>>();
		for ( var id = 0; id < instances.Count; ++id )
		{
			var instance = instances[id];
			ValidateInstance( instance );
			var cell = (Coordinate( instance.Position.x ), Coordinate( instance.Position.y ));
			if ( !cells.TryGetValue( cell, out var members ) ) cells.Add( cell, members = [] );
			members.Add( id );
			var model = (instance.ModelIndex, instance.Skin);
			if ( !models.TryGetValue( model, out members ) ) models.Add( model, members = [] );
			members.Add( id );
		}

		var cellIds = new int[instances.Count];
		var modelIds = new int[instances.Count];
		var offset = 0;
		var compiledCells = cells.OrderBy( x => x.Key.X ).ThenBy( x => x.Key.Y ).Select( pair =>
		{
			var cell = new Cell( pair.Key.X, pair.Key.Y, offset, pair.Value.Count );
			pair.Value.CopyTo( cellIds, offset );
			offset += pair.Value.Count;
			return cell;
		} ).ToArray();
		offset = 0;
		var compiledModels = models.OrderBy( x => x.Key.Model ).ThenBy( x => x.Key.Skin ).Select( pair =>
		{
			var model = new ModelGroup( pair.Key.Model, pair.Key.Skin, offset, pair.Value.Count );
			pair.Value.CopyTo( modelIds, offset );
			offset += pair.Value.Count;
			return model;
		} ).ToArray();
		return new( compiledCells, cellIds, compiledModels, modelIds );
	}

	static int Coordinate( float value ) => checked((int)MathF.Floor( value / CellSize ));

	static void ValidateInstance( Instance instance )
	{
		if ( !float.IsFinite( instance.Position.x ) || !float.IsFinite( instance.Position.y ) || !float.IsFinite( instance.Position.z )
			|| instance.ModelIndex < 0 || instance.Skin < 0 )
			throw new InvalidDataException( "Invalid compiled static instance." );
		_ = Coordinate( instance.Position.x );
		_ = Coordinate( instance.Position.y );
	}

	internal void Write( BinaryWriter writer )
	{
		writer.Write( Magic );
		writer.Write( CellSize );
		writer.Write( Cells.Length );
		foreach ( var cell in Cells )
		{
			writer.Write( cell.X ); writer.Write( cell.Y ); writer.Write( cell.First ); writer.Write( cell.Count );
		}
		foreach ( var id in CellInstances ) writer.Write( id );
		writer.Write( Models.Length );
		foreach ( var model in Models )
		{
			writer.Write( model.ModelIndex ); writer.Write( model.Skin ); writer.Write( model.First ); writer.Write( model.Count );
		}
		foreach ( var id in ModelInstances ) writer.Write( id );
	}

	internal static Titanfall2CompiledPropLayout Read( BinaryReader reader, IReadOnlyList<Instance> instances )
	{
		if ( reader.ReadInt32() != Magic || reader.ReadSingle() != CellSize )
			throw new InvalidDataException( "Unsupported compiled static scene layout." );
		var cellCount = ReadCount( reader, instances.Count );
		var cells = new Cell[cellCount];
		for ( var i = 0; i < cellCount; ++i ) cells[i] = new( reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() );
		var cellIds = new int[instances.Count];
		for ( var i = 0; i < cellIds.Length; ++i ) cellIds[i] = reader.ReadInt32();
		var modelCount = ReadCount( reader, instances.Count );
		var models = new ModelGroup[modelCount];
		for ( var i = 0; i < modelCount; ++i ) models[i] = new( reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() );
		var modelIds = new int[instances.Count];
		for ( var i = 0; i < modelIds.Length; ++i ) modelIds[i] = reader.ReadInt32();
		var result = new Titanfall2CompiledPropLayout( cells, cellIds, models, modelIds );
		result.Validate( instances );
		return result;
	}

	static int ReadCount( BinaryReader reader, int maximum )
	{
		var count = reader.ReadInt32();
		if ( count < 0 || count > maximum ) throw new InvalidDataException( "Invalid compiled group count." );
		return count;
	}

	void Validate( IReadOnlyList<Instance> instances )
	{
		foreach ( var instance in instances ) ValidateInstance( instance );
		var seen = new bool[instances.Count];
		var next = 0;
		(int X, int Y)? previousCell = null;
		foreach ( var cell in Cells )
		{
			if ( previousCell is { } previous && (cell.X < previous.X || (cell.X == previous.X && cell.Y <= previous.Y)) )
				throw new InvalidDataException( "Duplicate or unsorted compiled cell." );
			previousCell = (cell.X, cell.Y);
			ValidateRange( cell.First, cell.Count, ref next, instances.Count );
			for ( var i = cell.First; i < next; ++i )
			{
				var id = CellInstances[i];
				ValidateId( id, seen );
				if ( Coordinate( instances[id].Position.x ) != cell.X || Coordinate( instances[id].Position.y ) != cell.Y )
					throw new InvalidDataException( "Static instance is in the wrong compiled cell." );
			}
		}
		if ( next != instances.Count ) throw new InvalidDataException( "Incomplete compiled cell layout." );
		Array.Clear( seen );
		next = 0;
		(int Model, int Skin)? previousModel = null;
		foreach ( var model in Models )
		{
			if ( previousModel is { } previous && (model.ModelIndex < previous.Model || (model.ModelIndex == previous.Model && model.Skin <= previous.Skin)) )
				throw new InvalidDataException( "Duplicate or unsorted compiled model group." );
			previousModel = (model.ModelIndex, model.Skin);
			ValidateRange( model.First, model.Count, ref next, instances.Count );
			for ( var i = model.First; i < next; ++i )
			{
				var id = ModelInstances[i];
				ValidateId( id, seen );
				if ( instances[id].ModelIndex != model.ModelIndex || instances[id].Skin != model.Skin )
					throw new InvalidDataException( "Static instance is in the wrong compiled model group." );
			}
		}
		if ( next != instances.Count ) throw new InvalidDataException( "Incomplete compiled model layout." );
	}

	static void ValidateRange( int first, int count, ref int next, int length )
	{
		if ( first != next || count <= 0 || count > length - next )
			throw new InvalidDataException( "Invalid compiled group range." );
		next += count;
	}

	static void ValidateId( int id, bool[] seen )
	{
		if ( (uint)id >= seen.Length || seen[id] ) throw new InvalidDataException( "Invalid or duplicate compiled instance ID." );
		seen[id] = true;
	}
}
