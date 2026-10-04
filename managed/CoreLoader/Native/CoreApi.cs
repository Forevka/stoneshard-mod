using System.Runtime.InteropServices;

namespace CoreLoader.Native;

// Field-for-field mirror of `struct CoreApi` in src/host/core_api.h. The two MUST
// change together: append only, and bump the version when a meaning changes.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CoreApi
{
    public const int ExpectedVersion = 11;

    public int Size;
    public int Version;

    // loader
    public delegate* unmanaged<int, byte*, byte*, void> Log;
    public delegate* unmanaged<byte*> GameName;
    public delegate* unmanaged<byte*> GameDir;
    public delegate* unmanaged<byte*> LoaderDir;

    // symbols
    public delegate* unmanaged<int> SymbolCount;
    public delegate* unmanaged<int, byte*> SymbolName;
    public delegate* unmanaged<int, nint> SymbolAddress;
    public delegate* unmanaged<byte*, nint> SymbolFind;

    // GML bridge
    public delegate* unmanaged<int> GmlReady;
    public delegate* unmanaged<int> AbiProven;
    public delegate* unmanaged<nint> CurrentSelf;
    public delegate* unmanaged<nint, nint, nint, RValue*, RValue*, int, int> CallScript;
    public delegate* unmanaged<nint, nint, nint, int> CallEvent;
    public delegate* unmanaged<byte*, RValue*, RValue*, int, nint, nint, int> CallBuiltin;
    public delegate* unmanaged<int> BuiltinCount;
    public delegate* unmanaged<RValue*, byte*, int> SetString;
    public delegate* unmanaged<RValue*, byte*, int, int> ValueToString;
    public delegate* unmanaged<nint, byte*, RValue*, int> VarGet;
    public delegate* unmanaged<nint, byte*, RValue*, int> VarSet;

    // UI
    public delegate* unmanaged<byte*, int> UiBeginTabBar;
    public delegate* unmanaged<void> UiEndTabBar;
    public delegate* unmanaged<byte*, int> UiBeginTabItem;
    public delegate* unmanaged<void> UiEndTabItem;
    public delegate* unmanaged<byte*, void> UiText;
    public delegate* unmanaged<float, float, float, float, byte*, void> UiTextColored;
    public delegate* unmanaged<byte*, void> UiTextDisabled;
    public delegate* unmanaged<byte*, int> UiButton;
    public delegate* unmanaged<byte*, int*, int> UiCheckbox;
    public delegate* unmanaged<byte*, float*, float, float, int> UiSliderFloat;
    public delegate* unmanaged<byte*, int*, int> UiInputInt;
    public delegate* unmanaged<byte*, byte*, int, int> UiInputText;
    public delegate* unmanaged<byte*, int> UiCollapsingHeader;
    public delegate* unmanaged<void> UiSameLine;
    public delegate* unmanaged<void> UiSeparator;
    public delegate* unmanaged<byte*, void> UiPushId;
    public delegate* unmanaged<void> UiPopId;

    // hooks
    public delegate* unmanaged<nint, int, int> HookInstall;
    public delegate* unmanaged<int, int, int> HookSetManaged;
    public delegate* unmanaged<int> HookCount;

    public delegate* unmanaged<byte*, int> BuiltinArity;
    public delegate* unmanaged<CoreHookCall*, RValue*, int> HookCallOriginal;
    public delegate* unmanaged<int, byte*> BuiltinName;
    public delegate* unmanaged<RValue*, int> ValueFree;
    public delegate* unmanaged<RValue*, RValue*, int> ValueCopy;
    public delegate* unmanaged<int, int, int> HookEnable;

    // UI round 2
    public delegate* unmanaged<byte*, byte*, int, int, int> UiInputTextFlags;
    public delegate* unmanaged<byte*, float, int, int> UiBeginChild;
    public delegate* unmanaged<void> UiEndChild;
    public delegate* unmanaged<void> UiSetKeyboardFocusHere;
    public delegate* unmanaged<float, void> UiSetScrollHereY;
    public delegate* unmanaged<int, int> UiIsKeyPressed;
    public delegate* unmanaged<float> UiGetScrollY;
    public delegate* unmanaged<float> UiGetScrollMaxY;
    public delegate* unmanaged<byte*, byte*, int, byte**, int, int*, int> UiInputHistory;
    public delegate* unmanaged<nint, byte*, int, int> MemoryRead;
    public delegate* unmanaged<int, void> InputPickArm;
    public delegate* unmanaged<int*, int*, int*, int*, int*, int> InputPickTake;
    public delegate* unmanaged<byte*, int> UiTreeNode;
    public delegate* unmanaged<void> UiTreePop;
    public delegate* unmanaged<byte*, void> UiSetClipboard;
    public delegate* unmanaged<byte*, nint> BuiltinAddress;
    public delegate* unmanaged<int, byte*> BuiltinNameAt;

    // UI round 3
    public delegate* unmanaged<byte*, byte*, int> UiBeginCombo;
    public delegate* unmanaged<void> UiEndCombo;
    public delegate* unmanaged<byte*, int, int, int> UiSelectable;
    public delegate* unmanaged<byte*, void> UiSeparatorText;
    public delegate* unmanaged<byte*, double*, double, double, byte*, int> UiInputDouble;
    public delegate* unmanaged<byte*, int*, int, int, byte*, int> UiSliderInt;
    public delegate* unmanaged<float, void> UiSetNextItemWidth;
    public delegate* unmanaged<int, void> UiBeginDisabled;
    public delegate* unmanaged<void> UiEndDisabled;
    public delegate* unmanaged<byte*, byte*, byte*, int, int> UiInputTextHint;
    public delegate* unmanaged<float, float, void> UiSameLineEx;
    public delegate* unmanaged<byte*, void> UiTextWrapped;
    public delegate* unmanaged<void> UiSpacing;
    public delegate* unmanaged<byte*, float, float, int> UiButtonEx;
    public delegate* unmanaged<byte*, int> UiSmallButton;
    public delegate* unmanaged<float, float, byte*, void> UiProgressBar;
    public delegate* unmanaged<float, float, float, float, void> UiPushTextColor;
    public delegate* unmanaged<void> UiPopTextColor;
    public delegate* unmanaged<byte*, void> UiSetItemTooltip;
    public delegate* unmanaged<int, float, void> UiClipperBegin;
    public delegate* unmanaged<int*, int*, int> UiClipperStep;
    public delegate* unmanaged<void> UiClipperEnd;
    public delegate* unmanaged<int> UiIsItemDeactivatedAfterEdit;
    public delegate* unmanaged<byte*> LastGmlError;
    public delegate* unmanaged<RValue*, nint> InstanceFromId;
    // object types (version 11)
    public delegate* unmanaged<byte*> ObjtypeStatus;
    public delegate* unmanaged<byte*, int, int> ObjtypeDefine;
    public delegate* unmanaged<int, int, int, int> ObjtypeEvent;
    public delegate* unmanaged<CoreHookCall*, int> ObjtypeCallInherited;
}

// Mirror of `struct ManagedExports`.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ManagedExports
{
    public int Size;
    public delegate* unmanaged<void> Frame;
    public delegate* unmanaged<void> Gui;
    public delegate* unmanaged<void> Shutdown;
    public delegate* unmanaged<CoreHookCall*, void> HookDispatch;
}

// Mirror of `struct CoreHookCall` / mod::hk::Call.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CoreHookCall
{
    public nint Self;
    public nint Other;
    public RValue* Result;
    public RValue** Args;
    public int Argc;
    public int Phase;
    public int Skip;
    public int HookId;
}
