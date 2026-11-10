// The default is a retryable failure. Only a completed import can classify geometry
// as filtered or empty; null Model alone cannot distinguish those outcomes.
enum Titanfall2CollisionModelStatus { Unavailable, Loaded, Filtered, Empty }

readonly record struct Titanfall2CollisionModelResult(
	Titanfall2CollisionModelStatus Status, Model Model, uint ModelContents, string GeometrySource );
