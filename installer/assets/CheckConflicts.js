// =====================================================================
// CheckConflicts.js - MSI JScript custom actions (Binary-keyed file, no
// attribute-escaping hell).
//
// Conflict detection for the iCem Data Monitor installer.
//
// DETECTION LAW (2026-09-01 PM rewrite — bug report «проверка конфликтов не
// обновляется», MSI 1.4.3): the 2026-08-26 «name CONTAINS 'icem'» rule was a
// FALSE-POSITIVE MACHINE: it matched every Halliburton.iCem.* side process
// (LogUpload / LogUploadService / SyncService / Web) — but on that machine
// iCem.exe itself was NOT running, only service-hosted leftovers. Services
// own their processes: killing the image is a losing fight against the SCM
// (it restarts/re-spawns), the gate never went clean, «Обновить» kept
// showing the conflict, and only an msiexec restart passed. Operator proof:
// services Stopped + zero Halliburton.iCem.* processes + gate STILL red.
//
// New law:
//   PROCESS conflicts = EXACT image names (anchored, no substring):
//     icem.exe                     - the iCem host (loads our AddIn dll)
//     iCemDataMonitor.Host.exe     - our host (locks the monitor payload)
//     iCemDataMonitor.WinUI.exe    - our shell
//     iCemEmulator.exe             - our emulator
//     iCemDataMonitor.Cli.exe      - legacy cli name, in case it resurfaces
//   (the installer's own msiexec/wizard never matches an anchored name —
//   IsInstallerBundle is retired with the substring rule.)
//   SERVICE conflicts = Win32_Service rows whose Name or DisplayName starts
//   with 'icem', in a RUNNING state (Running / Start Pending /
//   Continue Pending). Stopped/Pause Pending/Paused services do NOT block
//   (the operator's Stop-Service x4 is honoured).
//
// «Закрыть всё» law (same rewrite): services first — StopService() per
// conflicting service (REQUEST_PENDING/Start Pending included: SCM cancels
// the start), then a settle wait; THEN process terminate for the exact-name
// set (iCem.exe and our stack — explicit owner decision 2026-08-26 that
// iCem may be killed by the gate). Only after the settle the CA returns and
// the gate re-checks (Order=2 publish). A service whose StopService leaves
// it Running (stuck StartPending after cancel refusal, AccessDenied) keeps
// ICEM_RUNNING=1 and the dialog text says which.
//
// Failure doctrine: WMI errors leave ICEM_RUNNING empty (gate open, the
// built-in FilesInUse dialog at InstallValidate is the backstop) —
// CheckConflicts NEVER aborts the install; CloseConflicts is best-effort.
//
// Entry points (JScriptCall): CheckConflicts, CloseConflicts,
// CancelAfterUserConfirm, WipeMonitorRoot.
// =====================================================================

var ICEM_SERVICE_QUERY =
    "SELECT Name, DisplayName, State FROM Win32_Service " +
    "WHERE Name LIKE 'iCem%' OR DisplayName LIKE 'iCem%'";

var SERVICE_STATES_RUNNING = {
    'Running': true,
    'Start Pending': true,
    'Continue Pending': true
};

// Pure name table (harness-testable, no WMI).
function IsConflictingProcessName(lowerName) {
    return lowerName === 'icem.exe' ||
        lowerName === 'icemdatamonitor.host.exe' ||
        lowerName === 'icemdatamonitor.winui.exe' ||
        lowerName === 'icememulator.exe' ||
        lowerName === 'icemdatamonitor.cli.exe';
}

function IsConflictingService(name, displayName, state) {
    if (!SERVICE_STATES_RUNNING[state]) { return false; }
    var n = String(name).toLowerCase();
    var d = String(displayName).toLowerCase();
    return n.indexOf('icem') === 0 || d.indexOf('icem') === 0;
}

function Wmi() {
    return GetObject('winmgmts:{impersonationLevel=impersonate}!\\\\.\\root\\cimv2');
}

// Returns { processes: [names], services: [display names] }.
function ConflictList() {
    var svc = Wmi();
    var found = { processes: [], services: [] };

    var e = new Enumerator(svc.InstancesOf('Win32_Process'));
    for (; !e.atEnd(); e.moveNext()) {
        var name = '';
        try { name = String(e.item().Name).toLowerCase(); } catch (e1) { }
        if (IsConflictingProcessName(name)) { found.processes.push(name); }
    }

    var q = new Enumerator(svc.ExecQuery(ICEM_SERVICE_QUERY));
    for (; !q.atEnd(); q.moveNext()) {
        var s = q.item();
        var sName = '', sDisp = '', sState = '';
        try { sName = String(s.Name); } catch (e2) { }
        try { sDisp = String(s.DisplayName); } catch (e3) { }
        try { sState = String(s.State); } catch (e4) { }
        if (IsConflictingService(sName, sDisp, sState)) {
            found.services.push(sDisp || sName);
        }
    }
    return found;
}

// Gate check: sets ICEM_RUNNING=1 when any conflict is alive, CLEARS it
// otherwise — the property must be REWRITTEN on every call (this is the
// «Обновить» mechanism: Publish DoAction re-runs this CA, the open dialog
// re-evaluates its control conditions against the fresh property value).
// 2026-09-01 PM: Return="check" in Product.wxs — if the JScript engine
// itself fails, the install ABORTS loudly instead of silently skipping the
// gate (the operator's «install passed with iCem running» class).
function CheckConflicts() {
    var running = '';
    try {
        var c = ConflictList();
        if (c.processes.length > 0 || c.services.length > 0) { running = '1'; }
    } catch (e2) { running = ''; }
    PutSessionProperty('ICEM_RUNNING', running);
}

// Inside the MSI the Session dispatch object supports the parameterized
// property put (Session.Property('X') = v). A plain-JS harness stub cannot
// express that, so the standalone test overrides HARNESS_SETPROP before
// eval'ing this file; production always takes the Session branch.
function PutSessionProperty(name, value) {
    if (typeof HARNESS_SETPROP !== 'undefined') { HARNESS_SETPROP(name, value); return; }
    Session.Property(name) = value;
}

// "Закрыть всё": stop conflicting SERVICES first (SCM owns their processes),
// settle, then terminate the exact-name process set (iCem.exe + our stack).
// Best effort, never throws.
function CloseConflicts() {
    try {
        var svc = Wmi();

        var q = new Enumerator(svc.ExecQuery(ICEM_SERVICE_QUERY));
        for (; !q.atEnd(); q.moveNext()) {
            var s = q.item();
            var sName = '', sDisp = '', sState = '';
            try { sName = String(s.Name); } catch (e2) { }
            try { sDisp = String(s.DisplayName); } catch (e3) { }
            try { sState = String(s.State); } catch (e4) { }
            if (IsConflictingService(sName, sDisp, sState)) {
                // StopService on a Start-Pending service asks the SCM to
                // cancel the start; a refused stop just stays Running and
                // the gate stays red with the honest name.
                try { s.StopService(); } catch (e5) { }
            }
        }
        try { WScript.Sleep(3000); } catch (e6) { }

        var e = new Enumerator(svc.InstancesOf('Win32_Process'));
        for (; !e.atEnd(); e.moveNext()) {
            var p = e.item();
            var name = '';
            try { name = String(p.Name).toLowerCase(); } catch (e7) { }
            if (IsConflictingProcessName(name)) {
                try { p.Terminate(); } catch (e8) { }
            }
        }
        try { WScript.Sleep(1000); } catch (e9) { }
    } catch (e0) { }
}

// 2026-09-01 PM (same bug report, second click-path): CancelDlg/Yes kills
// only the WIZARD (client) msiexec — the server process keeps any JScript
// session alive, and the report shows the «closed» processes of the dead
// wizard session still haunting the retry (operator killed msiexec.exe by
// hand to get unstuck). This CA runs on CancelDlg/Yes AND on
// UserExit: StopService + Terminate on the same conflict set, best effort.
// It is the symmetrical opposite of the gate: when the operator says
// «Отмена», nothing our wizard saw may outlive it.
function CancelAfterUserConfirm() {
    CloseConflicts();
}

// 2026-08-31 (owner decree «после деинсталяции — удалять за собой файлы»):
// full-uninstall wipe of the runtime folder (config.ini, Data.dat, Logs,
// emule_config.xml, WebView2 cache, legacy iCemEmulator\ subfolder). Deferred CA
// — reads ONLY CustomActionData (the absolute DataMonitoring path composed
// from [CommonAppDataFolder] by the matching SetProperty). Best effort,
// never throws: a locked file (still-running app, WebView2 cache) survives,
// the uninstall must never roll back over cleanup.
// HISTORY: this replaces util:RemoveFolderEx — on the stand its CA first
// failed with 0x80070057 «Missing folder property» (Directory-table ids
// resolve at CostFinalize, AFTER the WixRemoveFoldersEx action) and then
// reported «Path is a junction; skipping» on a plain directory and wiped
// nothing. A direct FSO delete has no such guards.
// Sequencing/conditions live in Product.wxs: fires on REMOVE="ALL" AND NOT
// UPGRADINGPRODUCTCODE — a real uninstall wipes, a MajorUpgrade keeps
// config.ini/Data.dat alive across the upgrade.
// 2026-09-01 PM: sequencing moved to Before="InstallFinalize" (after
// RemoveFiles) — the wipe must see the directory table already emptied, so
// no reinstall-window can resurrect files under it.
function WipeMonitorRoot() {
    try {
        var path = String(Session.Property('CustomActionData'));
        if (!path) { return; }
        var fso = new ActiveXObject('Scripting.FileSystemObject');
        if (fso.FolderExists(path)) { fso.DeleteFolder(path, true); }
    } catch (e) { }
}
