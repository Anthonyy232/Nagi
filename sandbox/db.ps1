# Reads and writes Nagi's database through the SQLite that ships with Windows (winsqlite3.dll). Dot-source it.
if (-not $NagiDatabase) { $NagiDatabase = "$env:LOCALAPPDATA\Packages\$((Get-AppxPackage 48743Nagi.Nagi).PackageFamilyName)\LocalState\nagi.db" }

if (-not ('Sqlite' -as [type])) { Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Sqlite {
  [DllImport("winsqlite3.dll", CharSet = CharSet.Ansi)] public static extern int sqlite3_open(string path, out IntPtr db);
  [DllImport("winsqlite3.dll", CharSet = CharSet.Ansi)] public static extern int sqlite3_exec(IntPtr db, string sql, IntPtr cb, IntPtr arg, out IntPtr err);
  [DllImport("winsqlite3.dll", CharSet = CharSet.Ansi)] public static extern int sqlite3_prepare_v2(IntPtr db, string sql, int n, out IntPtr stmt, IntPtr tail);
  [DllImport("winsqlite3.dll")] public static extern int sqlite3_step(IntPtr stmt);
  [DllImport("winsqlite3.dll")] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
  [DllImport("winsqlite3.dll")] public static extern int sqlite3_finalize(IntPtr stmt);
  [DllImport("winsqlite3.dll")] public static extern int sqlite3_close(IntPtr db);
  [DllImport("winsqlite3.dll")] public static extern IntPtr sqlite3_errmsg(IntPtr db);
}
'@ }

function Use-NagiDatabase([scriptblock] $Action) {
    $db = [IntPtr]::Zero
    if ([Sqlite]::sqlite3_open($NagiDatabase, [ref]$db)) { throw "Cannot open $NagiDatabase" }
    try { & $Action $db } finally { [Sqlite]::sqlite3_close($db) | Out-Null }
}

function Invoke-NagiSql([string] $Sql) {
    Use-NagiDatabase {
        param($db)
        $err = [IntPtr]::Zero
        if ([Sqlite]::sqlite3_exec($db, $Sql, [IntPtr]::Zero, [IntPtr]::Zero, [ref]$err)) {
            throw "SQL failed: $([Runtime.InteropServices.Marshal]::PtrToStringAnsi([Sqlite]::sqlite3_errmsg($db)))"
        }
    }
}

# The first column of the first row, as a number.
function Get-NagiNumber([string] $Sql) {
    Use-NagiDatabase {
        param($db)
        $stmt = [IntPtr]::Zero
        if ([Sqlite]::sqlite3_prepare_v2($db, $Sql, -1, [ref]$stmt, [IntPtr]::Zero)) {
            throw "SQL failed: $([Runtime.InteropServices.Marshal]::PtrToStringAnsi([Sqlite]::sqlite3_errmsg($db)))"
        }
        try { [Sqlite]::sqlite3_step($stmt) | Out-Null; [Sqlite]::sqlite3_column_int64($stmt, 0) } finally { [Sqlite]::sqlite3_finalize($stmt) | Out-Null }
    }
}
