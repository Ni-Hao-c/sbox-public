// Runtime bridge for the Miles/Bink Audio version shipped with Titanfall 2.
// The proprietary game DLLs are never linked or redistributed: both are loaded
// from the user's local Titanfall 2 installation and accessed through exports.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace
{
using MilesStartupFn = int(__cdecl*)(void*);
using MilesShutdownFn = void(__cdecl*)();
using MilesOutputGetMemoryFn = void*(__cdecl*)();
using MilesDriverCreateFn = void*(__cdecl*)(const void*);
using MilesDriverDestroyFn = void(__cdecl*)(void*);
using MilesDriverGetOutputFn = void*(__cdecl*)(void*, std::uint32_t);
using MilesDriverGetOutputChannelCountFn = std::uint32_t(__cdecl*)(void*, std::uint32_t);
using MilesSampleCreateFn = void*(__cdecl*)(void*, void*, std::uint32_t);
using MilesSampleDestroyFn = void(__cdecl*)(void*);
using MilesSampleSetSourceFn = int(__cdecl*)(void*, const void*, std::uint32_t, std::uint32_t);
using MilesSamplePlayFn = void(__cdecl*)(void*);
using MilesServiceDriversFn = std::uint32_t(__cdecl*)();
using MilesDriverRegisterBinkAudioFn = void(__cdecl*)(void*);

struct DriverConfig
{
	void* outputProvider;
	void* outputConfig;
	std::uint32_t channelSpec;
	std::uint32_t sampleRate;
	std::uint32_t createServiceThread;
	std::uint32_t reserved;
};
static_assert(sizeof(DriverConfig) == 32);

struct MemoryOutputConfig
{
	std::uint8_t* data;
	std::uint64_t size;
	volatile std::uint32_t completed;
	std::uint32_t reserved;
};
static_assert(sizeof(MemoryOutputConfig) == 24);

struct MilesApi
{
	HMODULE miles = nullptr;
	HMODULE bink = nullptr;
	MilesStartupFn startup = nullptr;
	MilesShutdownFn shutdown = nullptr;
	MilesOutputGetMemoryFn outputGetMemory = nullptr;
	MilesDriverCreateFn driverCreate = nullptr;
	MilesDriverDestroyFn driverDestroy = nullptr;
	MilesDriverGetOutputFn driverGetOutput = nullptr;
	MilesDriverGetOutputChannelCountFn driverGetOutputChannelCount = nullptr;
	MilesSampleCreateFn sampleCreate = nullptr;
	MilesSampleDestroyFn sampleDestroy = nullptr;
	MilesSampleSetSourceFn sampleSetSource = nullptr;
	MilesSamplePlayFn samplePlay = nullptr;
	MilesServiceDriversFn serviceDrivers = nullptr;
	MilesDriverRegisterBinkAudioFn registerBinkAudio = nullptr;
};

struct CaptureContext
{
	std::vector<std::int16_t> pcm;
	std::uint32_t targetFrames = 0;
};

std::mutex g_decodeMutex;
MilesApi g_api;
bool g_started = false;
std::wstring g_loadedRoot;

void SetError(char* destination, std::uint32_t capacity, const std::string& message)
{
	if (!destination || capacity == 0) return;
	const auto length = std::min<std::size_t>(message.size(), capacity - 1);
	std::memcpy(destination, message.data(), length);
	destination[length] = '\0';
}

std::string WindowsError(const char* prefix)
{
	return std::string(prefix) + " (Windows error " + std::to_string(GetLastError()) + ")";
}

template <typename T>
bool Resolve(HMODULE module, const char* name, T& target, std::string& error)
{
	target = reinterpret_cast<T>(GetProcAddress(module, name));
	if (target) return true;
	error = std::string("Missing Miles export: ") + name;
	return false;
}

void UnloadApi()
{
	if (g_started && g_api.shutdown) g_api.shutdown();
	g_started = false;
	if (g_api.bink) FreeLibrary(g_api.bink);
	if (g_api.miles) FreeLibrary(g_api.miles);
	g_api = {};
	g_loadedRoot.clear();
}

bool LoadApi(const wchar_t* gameRoot, std::string& error)
{
	if (!gameRoot || !*gameRoot)
	{
		error = "Titanfall 2 game root is empty.";
		return false;
	}

	const std::wstring root(gameRoot);
	if (g_started && _wcsicmp(root.c_str(), g_loadedRoot.c_str()) == 0) return true;
	UnloadApi();

	const auto bin = root + L"\\bin\\x64_retail";
	const auto milesPath = bin + L"\\mileswin64.dll";
	const auto binkPath = bin + L"\\binkawin64.dll";
	g_api.miles = LoadLibraryExW(milesPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
	if (!g_api.miles)
	{
		error = WindowsError("Unable to load Titanfall 2 mileswin64.dll");
		UnloadApi();
		return false;
	}

	if (!Resolve(g_api.miles, "MilesStartup", g_api.startup, error) ||
		!Resolve(g_api.miles, "MilesShutdown", g_api.shutdown, error) ||
		!Resolve(g_api.miles, "MilesOutputGetMemory", g_api.outputGetMemory, error) ||
		!Resolve(g_api.miles, "MilesDriverCreate", g_api.driverCreate, error) ||
		!Resolve(g_api.miles, "MilesDriverDestroy", g_api.driverDestroy, error) ||
		!Resolve(g_api.miles, "MilesDriverGetOutput", g_api.driverGetOutput, error) ||
		!Resolve(g_api.miles, "MilesDriverGetOutputChannelCount", g_api.driverGetOutputChannelCount, error) ||
		!Resolve(g_api.miles, "MilesSampleCreate", g_api.sampleCreate, error) ||
		!Resolve(g_api.miles, "MilesSampleDestroy", g_api.sampleDestroy, error) ||
		!Resolve(g_api.miles, "MilesSampleSetSource", g_api.sampleSetSource, error) ||
		!Resolve(g_api.miles, "MilesSamplePlay", g_api.samplePlay, error) ||
		!Resolve(g_api.miles, "MilesServiceDrivers", g_api.serviceDrivers, error))
	{
		UnloadApi();
		return false;
	}

	g_api.bink = LoadLibraryExW(binkPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
	if (!g_api.bink)
	{
		error = WindowsError("Unable to load Titanfall 2 binkawin64.dll");
		UnloadApi();
		return false;
	}
	if (!Resolve(g_api.bink, "MilesDriverRegisterBinkAudio", g_api.registerBinkAudio, error))
	{
		UnloadApi();
		return false;
	}

	// Passing null selects the reporting callback built into this Miles version.
	if (!g_api.startup(nullptr))
	{
		error = "MilesStartup failed.";
		UnloadApi();
		return false;
	}

	g_started = true;
	g_loadedRoot = root;
	return true;
}

void* CreateDriver(std::uint32_t channels, std::uint32_t sampleRate, MemoryOutputConfig* memoryOutput, std::uint32_t& actualChannels)
{
	// Channel-spec values are an enum rather than a count. Probe the small enum
	// and retain the first layout whose public channel count matches the source.
	for (std::uint32_t channelSpec = 0; channelSpec <= 12; ++channelSpec)
	{
		DriverConfig config{};
		config.outputProvider = g_api.outputGetMemory();
		config.outputConfig = memoryOutput;
		config.channelSpec = channelSpec;
		config.sampleRate = sampleRate;
		config.createServiceThread = 0;
		auto* driver = g_api.driverCreate(&config);
		if (!driver) continue;
		actualChannels = g_api.driverGetOutputChannelCount(driver, 0);
		if (actualChannels == channels) return driver;
		g_api.driverDestroy(driver);
	}
	return nullptr;
}
}

extern "C" __declspec(dllexport) int __cdecl tf2_miles_decode(
	const wchar_t* gameRoot,
	const std::uint8_t* source,
	std::uint32_t sourceLength,
	std::int16_t** outputPcm,
	std::uint32_t* outputFrames,
	std::uint32_t* outputRate,
	std::uint16_t* outputChannels,
	char* errorBuffer,
	std::uint32_t errorCapacity)
{
	if (outputPcm) *outputPcm = nullptr;
	if (outputFrames) *outputFrames = 0;
	if (outputRate) *outputRate = 0;
	if (outputChannels) *outputChannels = 0;
	if (!source || sourceLength < 24 || !outputPcm || !outputFrames || !outputRate || !outputChannels)
	{
		SetError(errorBuffer, errorCapacity, "Invalid Bink Audio decode arguments.");
		return 0;
	}
	if (std::memcmp(source, "1FCB", 4) != 0)
	{
		SetError(errorBuffer, errorCapacity, "The source is not a Titanfall 2 BCF stream.");
		return 0;
	}

	const auto channels = static_cast<std::uint32_t>(source[5]);
	std::uint16_t rate16 = 0;
	std::uint32_t frames = 0;
	std::uint32_t declaredSize = 0;
	std::memcpy(&rate16, source + 6, sizeof(rate16));
	std::memcpy(&frames, source + 8, sizeof(frames));
	std::memcpy(&declaredSize, source + 16, sizeof(declaredSize));
	if (channels == 0 || channels > 8 || rate16 < 8000 || frames == 0 || declaredSize != sourceLength)
	{
		SetError(errorBuffer, errorCapacity, "The BCF header is inconsistent with the source record.");
		return 0;
	}
	if (static_cast<std::uint64_t>(frames) * channels > SIZE_MAX / sizeof(std::int16_t))
	{
		SetError(errorBuffer, errorCapacity, "The decoded BCF stream is too large.");
		return 0;
	}

	std::lock_guard<std::mutex> guard(g_decodeMutex);
	std::string error;
	if (!LoadApi(gameRoot, error))
	{
		SetError(errorBuffer, errorCapacity, error);
		return 0;
	}

	// Miles' memory output in the Titanfall 2 build is stable for mono/stereo.
	// Surround sources are routed through Miles' own downmixer to stereo.
	const auto outputChannelTarget = channels > 2 ? 2u : channels;
	CaptureContext capture;
	capture.targetFrames = frames;
	try
	{
		const auto paddedFrames = (static_cast<std::size_t>(frames) + 63) & ~std::size_t(63);
		capture.pcm.resize(paddedFrames * outputChannelTarget);
	}
	catch (...)
	{
		SetError(errorBuffer, errorCapacity, "Unable to allocate the decoded PCM buffer.");
		return 0;
	}
	MemoryOutputConfig memoryOutput{};
	memoryOutput.data = reinterpret_cast<std::uint8_t*>(capture.pcm.data());
	memoryOutput.size = capture.pcm.size() * sizeof(std::int16_t);

	std::uint32_t actualChannels = 0;
	auto* driver = CreateDriver(outputChannelTarget, rate16, &memoryOutput, actualChannels);
	if (!driver)
	{
		SetError(errorBuffer, errorCapacity, "Miles could not create a memory-output driver with the requested channel layout.");
		return 0;
	}

	g_api.registerBinkAudio(driver);
	auto* output = g_api.driverGetOutput(driver, 0);
	auto* sample = g_api.sampleCreate(driver, nullptr, 0);
	if (!output || !sample)
	{
		if (sample) g_api.sampleDestroy(sample);
		g_api.driverDestroy(driver);
		SetError(errorBuffer, errorCapacity, "Miles could not create its output or sample object.");
		return 0;
	}

	if (!g_api.sampleSetSource(sample, source, sourceLength, 0x40))
	{
		g_api.sampleDestroy(sample);
		g_api.driverDestroy(driver);
		SetError(errorBuffer, errorCapacity, "Miles/Bink rejected the BCF stream.");
		return 0;
	}
	g_api.samplePlay(sample);

	const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
	std::uint32_t idlePasses = 0;
	while (memoryOutput.completed == 0 && std::chrono::steady_clock::now() < deadline)
	{
		g_api.serviceDrivers();
		if (++idlePasses > 1'000'000) std::this_thread::yield();
	}

	g_api.sampleDestroy(sample);
	g_api.driverDestroy(driver);
	if (memoryOutput.completed == 0)
	{
		SetError(errorBuffer, errorCapacity, "Miles/Bink did not fill the offline PCM output buffer.");
		return 0;
	}

	const auto bytes = static_cast<std::size_t>(capture.targetFrames) * outputChannelTarget * sizeof(std::int16_t);
	auto* result = static_cast<std::int16_t*>(std::malloc(bytes));
	if (!result)
	{
		SetError(errorBuffer, errorCapacity, "Unable to allocate the returned PCM buffer.");
		return 0;
	}
	std::memcpy(result, capture.pcm.data(), bytes);
	*outputPcm = result;
	*outputFrames = capture.targetFrames;
	*outputRate = rate16;
	*outputChannels = static_cast<std::uint16_t>(actualChannels);
	SetError(errorBuffer, errorCapacity, "");
	return 1;
}

extern "C" __declspec(dllexport) void __cdecl tf2_miles_free(void* memory)
{
	std::free(memory);
}
