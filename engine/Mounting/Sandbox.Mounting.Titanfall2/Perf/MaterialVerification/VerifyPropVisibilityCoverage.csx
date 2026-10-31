var rows = new System.Collections.Generic.List<object>();
void Check( string name, bool passed ) => rows.Add( new { name, passed } );
var visibility = new Titanfall2.Titanfall2BspReader.BspVisibility
{
	CellCount = 2, WorldMeshCount = 1,
	Planes = new[] { new Titanfall2.Titanfall2BspReader.VisibilityPlane( System.Numerics.Vector3.UnitX, 0 ) },
	Nodes = new[] { new Titanfall2.Titanfall2BspReader.VisibilityBspNode( 0, 1 ), new Titanfall2.Titanfall2BspReader.VisibilityBspNode( -1, 0 ), new Titanfall2.Titanfall2BspReader.VisibilityBspNode( -1, 1 ) },
	ObjectCellMasks = new[] { default( Titanfall2.Titanfall2BspReader.VisibilityCellMask ), new Titanfall2.Titanfall2BspReader.VisibilityCellMask().Add( 0 ), default }
};
Check( "missing-origin-cell-is-added-without-removing-authored-coverage", visibility.GetPropCellMask( 0, -System.Numerics.Vector3.UnitX ) == new Titanfall2.Titanfall2BspReader.VisibilityCellMask( 3, 0 ) );
Check( "valid-original-coverage-is-unchanged", visibility.GetPropCellMask( 0, System.Numerics.Vector3.UnitX ) == new Titanfall2.Titanfall2BspReader.VisibilityCellMask( 1, 0 ) );
Check( "empty-mask-stays-unrestricted", visibility.GetPropCellMask( 1, System.Numerics.Vector3.UnitX ).IsEmpty );
Check( "invalid-origin-does-not-invent-cells", visibility.GetPropCellMask( 0, new System.Numerics.Vector3( float.NaN ) ) == new Titanfall2.Titanfall2BspReader.VisibilityCellMask( 1, 0 ) );
Check( "missing-object-mask-stays-unrestricted", visibility.GetPropCellMask( 100, System.Numerics.Vector3.UnitX ).IsEmpty );
var output = System.IO.Path.Combine( exchangeDirectory, "prop-visibility-coverage-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };
