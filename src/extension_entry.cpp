// A single, side-effect-free export that exists only so the loader can be
// registered as a GameMaker *extension* instead of a version.dll proxy.
//
// GameMaker resolves (GetProcAddress) every function a DLL extension declares
// when it initialises that extension at start-up. That resolve is what makes
// the runner LoadLibrary this DLL in the first place, and the load triggers
// DllMain, where the loader already boots itself on its own thread exactly as
// it does on the proxy route (see src/dllmain.cpp).
//
// The function is never meant to be called from GML - it only has to exist so
// the resolve succeeds and the runner does not report a missing extension
// function. Returning a constant is enough; on x64 the single calling
// convention means the declared signature never has to match anything.
extern "C" double coreloader_extension_probe(void) {
    return 1.0;
}
