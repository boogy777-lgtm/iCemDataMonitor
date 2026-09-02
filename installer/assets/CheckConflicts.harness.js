// CheckConflicts.harness.js — standalone cscript harness for the MSI gate
// logic (2026-09-01 PM, bug report «проверка конфликтов не обновляется»).
// Run: cscript //nologo installer\assets\CheckConflicts.harness.js
// Exit code 0 = all assertions passed; 1 = first failure (line printed).
//
// The production file is eval'ed; WMI is stubbed so the harness controls the
// process/service lists. HARNESS_SETPROP (declared by CheckConflicts.js) is
// the Session.Property sink.

var failures = 0;
var assertions = 0;
function Assert(cond, label) {
    assertions++;
    if (!cond) {
        failures++;
        WScript.Echo('FAIL: ' + label);
    }
}

// ---- WMI stub --------------------------------------------------------------
var FAKE_PROCESSES = [];   // [{ Name: '...' }]
var FAKE_SERVICES = [];    // [{ Name, DisplayName, State }]

function FakeWmi() {
    // The production code wraps the results in `new Enumerator(...)` — JScript
    // Enumerator accepts a plain JS array, so the stub returns arrays as-is.
    return {
        InstancesOf: function () { return FAKE_PROCESSES; },
        ExecQuery: function () { return FAKE_SERVICES; }
    };
}

var HARNESS_PROPS = {};
function HARNESS_SETPROP(name, value) { HARNESS_PROPS[name] = value; }

// ---- load the production script -------------------------------------------
var fso = new ActiveXObject('Scripting.FileSystemObject');
var here = fso.GetParentFolderName(WScript.ScriptFullName);
var src = fso.OpenTextFile(fso.BuildPath(here, 'CheckConflicts.js'), 1).ReadAll();
// The production file binds GetObject('winmgmts:...') inside Wmi(); swap the
// Wmi body for the stub BEFORE eval.
src = src.replace(
    /function Wmi\(\) \{[\s\S]*?\n\}/,
    'function Wmi() { return __HARNESS_WMI; }');
if (src.indexOf('__HARNESS_WMI') < 0) {
    WScript.Echo('FAIL: Wmi() replacement anchor not found — CheckConflicts.js changed shape');
    WScript.Quit(1);
}
var __HARNESS_WMI = FakeWmi();
eval(src);

// ---- pure name table --------------------------------------------------------
Assert(IsConflictingProcessName('icem.exe') === true, 'icem.exe conflicts');
Assert(IsConflictingProcessName('icemdatamonitor.host.exe') === true, 'our host conflicts');
Assert(IsConflictingProcessName('icemdatamonitor.winui.exe') === true, 'our shell conflicts');
Assert(IsConflictingProcessName('icememulator.exe') === true, 'our emulator conflicts');
Assert(IsConflictingProcessName('halliburton.icem.web.exe') === false,
    '2026-09-01 regression: Halliburton.iCem.Web.exe must NOT conflict (1.4.3 substring bug)');
Assert(IsConflictingProcessName('halliburton.icem.logupload.exe') === false,
    'Halliburton.iCem.LogUpload.exe must NOT conflict');
Assert(IsConflictingProcessName('halliburton.icem.syncservice.exe') === false,
    'Halliburton.iCem.SyncService.exe must NOT conflict');
Assert(IsConflictingProcessName('icemdatamonitor-1.4.5-x64.exe') === false,
    'the installer bundle itself never conflicts');
Assert(IsConflictingProcessName('msiexec.exe') === false, 'msiexec never conflicts');
Assert(IsConflictingProcessName('eterlogicvspedevicemanagerservice.exe') === false,
    'VSPE service (path contains iCem on some stands) never conflicts by name');

// ---- service rule -----------------------------------------------------------
Assert(IsConflictingService('iCem Lan Streamer', 'iCem Lan Streamer', 'Running') === true,
    'running iCem service conflicts');
Assert(IsConflictingService('iCemLogUpload', 'iCem Log Upload', 'Start Pending') === true,
    'Start Pending iCem service conflicts (SCM can cancel the start)');
Assert(IsConflictingService('iCem MongoDB', 'iCem MongoDB', 'Stopped') === false,
    'STOPPED iCem service never blocks (operator proof in the report)');
Assert(IsConflictingService('iCemSyncService', 'iCem Remote Data Sync', 'Paused') === false,
    'paused iCem service never blocks');
Assert(IsConflictingService('Winmgmt', 'Windows Management Instrumentation', 'Running') === false,
    'non-iCem service never conflicts');

// ---- the «Обновить» semantic: property REWRITTEN on every call -------------
FAKE_PROCESSES = [{ Name: 'iCem.exe' }];
FAKE_SERVICES = [];
CheckConflicts();
Assert(HARNESS_PROPS['ICEM_RUNNING'] === '1', 'iCem.exe running -> gate red');

FAKE_PROCESSES = [];
FAKE_SERVICES = [{ Name: 'iCemLogUpload', DisplayName: 'iCem Log Upload', State: 'Running' }];
CheckConflicts();
Assert(HARNESS_PROPS['ICEM_RUNNING'] === '1', 'running iCem service alone -> gate red');

// The report's end state: services stopped, zero processes -> gate GREEN.
FAKE_SERVICES = [{ Name: 'iCemLogUpload', DisplayName: 'iCem Log Upload', State: 'Stopped' }];
CheckConflicts();
Assert(HARNESS_PROPS['ICEM_RUNNING'] === '',
    'services stopped + no processes -> ICEM_RUNNING cleared (refresh works)');

// WMI failure -> property cleared, gate open, no throw (FilesInUse backstop).
__HARNESS_WMI = {
    InstancesOf: function () { throw new Error('WMI dead'); },
    ExecQuery: function () { throw new Error('WMI dead'); }
};
CheckConflicts();
Assert(HARNESS_PROPS['ICEM_RUNNING'] === '', 'WMI failure -> gate open, no throw');

WScript.Echo('harness: ' + (assertions - failures) + '/' + assertions + ' passed');
WScript.Quit(failures === 0 ? 0 : 1);
