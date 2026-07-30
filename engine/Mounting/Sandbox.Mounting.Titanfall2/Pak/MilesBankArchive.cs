using System.Buffers.Binary;
using System.Text;

/// <summary>
/// Read-only index for Titanfall 2 Miles v13 MBNK metadata and its split MSTR
/// Bink Audio payloads. Stream bytes are only read when a sound is requested.
/// </summary>
sealed class MilesBankArchive
{
	const int BankHeaderSize = 0xc0;
	const int SourceRecordSize = 0x58;
	const ushort CommonLanguage = ushort.MaxValue;
	const ushort TChineseLanguage = 9;

	readonly string _gameRoot;
	readonly IReadOnlyDictionary<(ushort Language, ushort Patch), MilesStreamFile> _streams;
	readonly IReadOnlyList<MilesSoundAsset> _assets;

	MilesBankArchive(
		string gameRoot,
		Dictionary<(ushort Language, ushort Patch), MilesStreamFile> streams,
		List<MilesSoundAsset> assets,
		ushort preferredLanguage )
	{
		_gameRoot = gameRoot;
		_streams = streams;
		_assets = assets;
		PreferredLanguage = preferredLanguage;
	}

	public IEnumerable<MilesSoundAsset> Assets => _assets;
	public ushort PreferredLanguage { get; }

	public static bool TryOpen( string gameRoot, string bankPath, out MilesBankArchive archive, out string error )
	{
		archive = null;
		error = null;
		try
		{
			var bank = File.ReadAllBytes( bankPath );
			if ( bank.Length < BankHeaderSize || !bank.AsSpan( 0, 4 ).SequenceEqual( "KNBC"u8 ) )
				throw new InvalidDataException( "Unsupported MBNK signature." );
			if ( ReadUInt32( bank, 4 ) != 13 )
				throw new InvalidDataException( $"Unsupported MBNK version {ReadUInt32( bank, 4 )}." );
			if ( ReadUInt32( bank, 8 ) != bank.Length )
				throw new InvalidDataException( "The MBNK declared size does not match the file." );

			var sourceTableOffset = ReadOffset( bank, 0x48 );
			var nextTableOffset = ReadOffset( bank, 0x58 );
			var stringTableOffset = ReadOffset( bank, 0x70 );
			if ( sourceTableOffset < BankHeaderSize || nextTableOffset <= sourceTableOffset || stringTableOffset >= bank.Length )
				throw new InvalidDataException( "The MBNK table offsets are invalid." );
			var sourceBytes = nextTableOffset - sourceTableOffset;
			if ( sourceBytes % SourceRecordSize != 0 )
				throw new InvalidDataException( "The MBNK v13 source table is not aligned to 0x58-byte records." );

			var totalRecords = sourceBytes / SourceRecordSize;
			if ( totalRecords <= 0 || totalRecords > 1_000_000 )
				throw new InvalidDataException( "The MBNK source record count is unreasonable." );

			var soundDirectory = Path.GetDirectoryName( bankPath ) ?? throw new InvalidDataException( "The MBNK has no parent directory." );
			var bankName = Path.GetFileNameWithoutExtension( bankPath );
			var streams = ReadStreams( soundDirectory, bankName );
			if ( streams.Count == 0 ) throw new InvalidDataException( "No matching MSTR streams were found." );

			var localizedLanguages = streams.Keys
				.Select( static key => key.Language )
				.Where( static language => language != CommonLanguage )
				.Distinct()
				.OrderBy( static language => language )
				.ToArray();
			var preferredLanguage = localizedLanguages.Contains( TChineseLanguage )
				? TChineseLanguage
				: localizedLanguages.FirstOrDefault( CommonLanguage );

			var selected = new Dictionary<string, MilesSoundAsset>( StringComparer.OrdinalIgnoreCase );
			for ( var recordIndex = 0; recordIndex < totalRecords; recordIndex++ )
			{
				var offset = checked(sourceTableOffset + recordIndex * SourceRecordSize);
				var language = ReadUInt16( bank, offset + 0x50 );
				if ( language != CommonLanguage && language != preferredLanguage ) continue;
				var patch = ReadUInt16( bank, offset + 0x52 );
				if ( !streams.TryGetValue( (language, patch), out var stream ) ) continue;

				var channels = bank[offset + 0x18];
				var rate = ReadUInt16( bank, offset + 0x14 );
				var fileSize = ReadUInt32( bank, offset + 0x28 );
				var streamHeaderSize = ReadUInt32( bank, offset + 0x30 );
				var sampleCount = ReadUInt32( bank, offset + 0x34 );
				var streamHeaderOffset = ReadUInt64( bank, offset + 0x38 );
				var streamDataOffset = ReadUInt64( bank, offset + 0x40 );
				if ( channels is 0 or > 8 || rate < 8000 || fileSize < 24 || streamHeaderSize < 24 || streamHeaderSize > fileSize || sampleCount == 0 ) continue;
				if ( streamHeaderOffset > (ulong)stream.DataOffset || streamHeaderSize > (ulong)stream.DataOffset - streamHeaderOffset ) continue;
				var payloadSize = fileSize - streamHeaderSize;
				if ( streamDataOffset > (ulong)(stream.Length - stream.DataOffset) || payloadSize > (ulong)(stream.Length - stream.DataOffset) - streamDataOffset ) continue;

				var nameOffset = ReadUInt32( bank, offset + 0x10 );
				if ( nameOffset >= bank.Length - stringTableOffset ) continue;
				var name = ReadString( bank, stringTableOffset + checked((int)nameOffset) );
				if ( !TryNormalizeName( name, out var resourceName ) ) continue;

				var asset = new MilesSoundAsset(
					resourceName,
					language,
					patch,
					channels,
					rate,
					fileSize,
					streamHeaderSize,
					sampleCount,
					streamHeaderOffset,
					streamDataOffset );
				if ( !selected.TryGetValue( resourceName, out var existing ) || existing.Language == CommonLanguage && language == preferredLanguage )
					selected[resourceName] = asset;
			}

			var assets = selected.Values.OrderBy( static asset => asset.ResourceName, StringComparer.OrdinalIgnoreCase ).ToList();
			if ( assets.Count == 0 ) throw new InvalidDataException( "The MBNK contains no sources for the installed MSTR language." );
			archive = new MilesBankArchive( gameRoot, streams, assets, preferredLanguage );
			return true;
		}
		catch ( Exception e )
		{
			error = e.Message;
			return false;
		}
	}

	public bool TryReadBcf( MilesSoundAsset asset, out byte[] bytes, out string error )
	{
		bytes = null;
		error = null;
		try
		{
			if ( !_streams.TryGetValue( (asset.Language, asset.Patch), out var streamFile ) )
				throw new InvalidDataException( $"MSTR language {asset.Language}, patch {asset.Patch} is unavailable." );
			bytes = GC.AllocateUninitializedArray<byte>( checked((int)asset.FileSize) );
			using var stream = new FileStream( streamFile.Path, FileMode.Open, FileAccess.Read, FileShare.Read );
			stream.Position = checked((long)asset.StreamHeaderOffset);
			stream.ReadExactly( bytes.AsSpan( 0, checked((int)asset.StreamHeaderSize) ) );
			stream.Position = checked(streamFile.DataOffset + (long)asset.StreamDataOffset);
			stream.ReadExactly( bytes.AsSpan( checked((int)asset.StreamHeaderSize) ) );

			// The bank's routing channel/sample counts can intentionally differ from
			// the encoded BCF header (for example, four named 2ch sources are routed
			// as mono). The codec header is authoritative for decode format.
			if ( !bytes.AsSpan( 0, 4 ).SequenceEqual( "1FCB"u8 ) ||
				ReadUInt32( bytes, 0x10 ) != asset.FileSize || ReadUInt16( bytes, 6 ) != asset.SampleRate )
				throw new InvalidDataException( "The reconstructed BCF header does not match its MBNK source record." );
			return true;
		}
		catch ( Exception e )
		{
			bytes = null;
			error = e.Message;
			return false;
		}
	}

	public bool TryDecode( byte[] bcf, out byte[] pcm, out uint rate, out ushort channels, out string error )
		=> MilesBinkDecoder.TryDecode( _gameRoot, bcf, out pcm, out rate, out channels, out error );

	static Dictionary<(ushort Language, ushort Patch), MilesStreamFile> ReadStreams( string directory, string bankName )
	{
		var result = new Dictionary<(ushort Language, ushort Patch), MilesStreamFile>();
		foreach ( var path in System.IO.Directory.EnumerateFiles( directory, $"{bankName}*.mstr", SearchOption.TopDirectoryOnly ) )
		{
			using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read );
			var header = new byte[32];
			if ( stream.Read( header ) != header.Length || !header[..4].SequenceEqual( "RTSC"u8 ) || BinaryPrimitives.ReadUInt16LittleEndian( header[4..] ) != 2 ) continue;
			var language = BinaryPrimitives.ReadUInt16LittleEndian( header[6..] );
			var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian( header[8..] );
			var patch = BinaryPrimitives.ReadInt32LittleEndian( header[12..] );
			if ( patch is < 0 or > ushort.MaxValue || dataOffset < 32 || dataOffset > stream.Length ) continue;
			result[(language, (ushort)patch)] = new MilesStreamFile( path, stream.Length, dataOffset );
		}
		return result;
	}

	static bool TryNormalizeName( string name, out string normalized )
	{
		normalized = null;
		if ( string.IsNullOrWhiteSpace( name ) || name.Any( char.IsControl ) ) return false;
		name = name.Replace( '\\', '/' ).Trim( '/' );
		if ( name.Length == 0 || name.Contains( ':', StringComparison.Ordinal ) || name.Split( '/' ).Any( static part => part is "" or "." or ".." ) ) return false;
		normalized = "sound/" + name;
		return true;
	}

	static string ReadString( byte[] data, int offset )
	{
		var end = offset;
		var limit = Math.Min( data.Length, offset + 1024 );
		while ( end < limit && data[end] != 0 ) end++;
		return end == limit ? string.Empty : Encoding.UTF8.GetString( data, offset, end - offset );
	}

	static int ReadOffset( byte[] data, int offset )
	{
		var value = ReadUInt64( data, offset );
		if ( value > int.MaxValue || value > (ulong)data.Length ) throw new InvalidDataException( "MBNK table offset exceeds the file." );
		return (int)value;
	}

	static ushort ReadUInt16( byte[] data, int offset ) => BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( offset, 2 ) );
	static uint ReadUInt32( byte[] data, int offset ) => BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( offset, 4 ) );
	static ulong ReadUInt64( byte[] data, int offset ) => BinaryPrimitives.ReadUInt64LittleEndian( data.AsSpan( offset, 8 ) );
}

sealed record MilesSoundAsset(
	string ResourceName,
	ushort Language,
	ushort Patch,
	byte Channels,
	ushort SampleRate,
	uint FileSize,
	uint StreamHeaderSize,
	uint SampleCount,
	ulong StreamHeaderOffset,
	ulong StreamDataOffset );

readonly record struct MilesStreamFile( string Path, long Length, long DataOffset );
