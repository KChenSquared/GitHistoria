namespace Historia.Tests;

[SupportedOSPlatform("windows")]
public sealed class CmdrPathServiceTests
{
    private sealed class FakeEnvironmentStore : IUserEnvironmentStore
    {
        public string? UserPath { get; set; }
        public RegistryValueKind UserKind { get; set; } = RegistryValueKind.ExpandString;
        public string? MachinePath { get; set; }
        public string? ProcessPath { get; set; } = @"C:\Windows";
        public int BroadcastCount { get; private set; }
        public List<(string Value, RegistryValueKind Kind)> Writes { get; } = [];
        public bool ThrowOnWrite { get; set; }
        public bool ThrowOnRead { get; set; }
        public bool ThrowOnMachineRead { get; set; }

        public (string? Value, RegistryValueKind Kind) ReadUserPath()
        {
            if (ThrowOnRead)
                throw new UnauthorizedAccessException("Registry read denied.");
            return (UserPath, UserKind);
        }

        public string? ReadMachinePath()
        {
            if (ThrowOnMachineRead)
                throw new UnauthorizedAccessException("Machine registry read denied.");
            return MachinePath;
        }

        public void WriteUserPath(string value, RegistryValueKind kind)
        {
            if (ThrowOnWrite)
                throw new UnauthorizedAccessException("Registry access denied.");

            UserPath = value;
            UserKind = kind;
            Writes.Add((value, kind));
        }

        public void BroadcastEnvironmentChange()
        {
            BroadcastCount++;
        }

        public string? ReadProcessPath() => ProcessPath;

        public void WriteProcessPath(string value) => ProcessPath = value;
    }

    [Fact]
    public void Status_is_Unavailable_when_our_folder_has_no_exe()
    {
        var store = new FakeEnvironmentStore();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ourFolder = @"C:\HistoriaApp";

        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var status = service.GetStatus();
        Assert.IsType<CmdrPathStatus.Unavailable>(status);
    }

    [Fact]
    public void Status_is_Current_when_ours_is_the_only_cmdr_entry()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = $@"C:\Tools;{ourFolder};C:\Bin",
        };

        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var status = service.GetStatus();
        Assert.IsType<CmdrPathStatus.Current>(status);
    }

    [Fact]
    public void Apply_writes_ExpandString_back_as_ExpandString_and_String_as_String()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };

        // Test ExpandString
        var storeExpand = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
            UserKind = RegistryValueKind.ExpandString,
        };
        var serviceExpand = new CmdrPathService(
            storeExpand,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup1.reg"),
            AppLog.Current);

        var previewExpand = ((CmdrPathStatus.NeedsUpdate)serviceExpand.GetStatus()).Preview;
        var resultExpand = serviceExpand.Apply(previewExpand);
        Assert.IsType<CmdrPathApplyResult.Written>(resultExpand);
        Assert.Equal(RegistryValueKind.ExpandString, storeExpand.UserKind);
        Assert.Equal($@"C:\Tools;{ourFolder}", storeExpand.UserPath);

        // Test String
        var storeString = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
            UserKind = RegistryValueKind.String,
        };
        var serviceString = new CmdrPathService(
            storeString,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup2.reg"),
            AppLog.Current);

        var previewString = ((CmdrPathStatus.NeedsUpdate)serviceString.GetStatus()).Preview;
        var resultString = serviceString.Apply(previewString);
        Assert.IsType<CmdrPathApplyResult.Written>(resultString);
        Assert.Equal(RegistryValueKind.String, storeString.UserKind);
        Assert.Equal($@"C:\Tools;{ourFolder}", storeString.UserPath);
    }

    [Fact]
    public void Missing_value_is_created_as_ExpandString()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = null,
            UserKind = RegistryValueKind.ExpandString,
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;
        var result = service.Apply(preview);

        Assert.IsType<CmdrPathApplyResult.Written>(result);
        Assert.Equal(ourFolder, store.UserPath);
        Assert.Equal(RegistryValueKind.ExpandString, store.UserKind);
    }

    [Fact]
    public void Broadcast_once_per_write()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;
        Assert.Equal(0, store.BroadcastCount);

        service.Apply(preview);
        Assert.Equal(1, store.BroadcastCount);
    }

    [Fact]
    public void No_write_and_Changed_when_store_value_changed_after_preview()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;

        // External change before apply
        store.UserPath = @"C:\Tools;C:\Other";

        var result = service.Apply(preview);
        Assert.IsType<CmdrPathApplyResult.Changed>(result);
        Assert.Empty(store.Writes);
        Assert.Equal(0, store.BroadcastCount);
    }

    [Fact]
    public void Failed_no_throw_when_store_throws_UnauthorizedAccessException()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
            ThrowOnWrite = true,
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;
        var result = service.Apply(preview);

        var failed = Assert.IsType<CmdrPathApplyResult.Failed>(result);
        Assert.Contains("denied", failed.Message);
    }

    [Fact]
    public void Machine_copies_listed_and_never_written()
    {
        var ourFolder = @"C:\HistoriaApp";
        var machineCmdrFolder = @"C:\ProgramData\OtherApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
            Path.Combine(machineCmdrFolder, "historia-cmdr.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
            MachinePath = $@"C:\Windows;{machineCmdrFolder}",
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var status = Assert.IsType<CmdrPathStatus.NeedsUpdate>(service.GetStatus());
        Assert.Equal([machineCmdrFolder], status.MachineCopies);

        service.Apply(status.Preview);
        Assert.Equal($@"C:\Windows;{machineCmdrFolder}", store.MachinePath);
    }

    [Fact]
    public void Backup_is_saved_before_write_and_holds_pre_write_value()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var initialUserPath = @"C:\Tools;C:\OldStuff";
        var store = new FakeEnvironmentStore
        {
            UserPath = initialUserPath,
            UserKind = RegistryValueKind.ExpandString,
        };

        string? capturedBackupContent = null;
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) =>
            {
                capturedBackupContent = content;
                return Path.Combine(folder, "backup.reg");
            },
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;
        var result = service.Apply(preview);

        Assert.IsType<CmdrPathApplyResult.Written>(result);
        Assert.NotNull(capturedBackupContent);

        var (restored, kind) = PathBackup.FromRegFile(capturedBackupContent);
        Assert.Equal(initialUserPath, restored);
        Assert.Equal(RegistryValueKind.ExpandString, kind);
    }

    [Fact]
    public void Throwing_saveBackup_returns_Failed_and_no_registry_write()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => throw new IOException("Disk full"),
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;
        var result = service.Apply(preview);

        var failed = Assert.IsType<CmdrPathApplyResult.Failed>(result);
        Assert.Contains("Disk full", failed.Message);
        Assert.Empty(store.Writes);
        Assert.Equal(0, store.BroadcastCount);
    }

    [Fact]
    public void Written_carries_backup_path()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
        };
        var expectedPath = @"C:\Backups\user-path-20261009-120000.reg";
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => expectedPath,
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;
        var result = service.Apply(preview);

        var written = Assert.IsType<CmdrPathApplyResult.Written>(result);
        Assert.Equal(expectedPath, written.BackupPath);
    }

    [Fact]
    public void Status_is_Unavailable_when_user_path_read_throws()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            ThrowOnRead = true,
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var status = service.GetStatus();
        Assert.IsType<CmdrPathStatus.Unavailable>(status);
    }

    [Fact]
    public void Machine_path_read_failure_gives_no_machine_copies()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
            ThrowOnMachineRead = true,
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var status = Assert.IsType<CmdrPathStatus.NeedsUpdate>(service.GetStatus());
        Assert.Empty(status.MachineCopies);
    }

    [Fact]
    public void Apply_returns_Failed_when_user_path_read_throws()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;

        store.ThrowOnRead = true;
        var result = service.Apply(preview);

        var failed = Assert.IsType<CmdrPathApplyResult.Failed>(result);
        Assert.Contains("Registry read denied", failed.Message);
    }

    [Fact]
    public void Apply_twice_with_the_same_preview_writes_once_and_then_reports_AlreadyCurrent()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
        };
        int backupCalls = 0;
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) =>
            {
                backupCalls++;
                return Path.Combine(folder, "backup.reg");
            },
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;

        var result1 = service.Apply(preview);
        Assert.IsType<CmdrPathApplyResult.Written>(result1);
        Assert.Single(store.Writes);
        Assert.Equal(1, backupCalls);
        Assert.Equal(1, store.BroadcastCount);

        var result2 = service.Apply(preview);
        Assert.IsType<CmdrPathApplyResult.AlreadyCurrent>(result2);
        Assert.Single(store.Writes);
        Assert.Equal(1, backupCalls);
        Assert.Equal(1, store.BroadcastCount);
    }

    [Fact]
    public void Trailing_separator_on_our_folder_is_not_written()
    {
        var ourFolder = @"C:\HistoriaApp\";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\HistoriaApp\historia-cmdr.exe",
            @"C:\HistoriaApp\Historia.exe",
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;
        Assert.Equal(@"C:\Tools;C:\HistoriaApp", preview.NewValue);
    }

    [Fact]
    public void NeedsUpdate_carries_our_folder_and_backup_folder()
    {
        var ourFolder = @"C:\HistoriaApp";
        var backupFolder = @"C:\Backups";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            backupFolder,
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var update = Assert.IsType<CmdrPathStatus.NeedsUpdate>(service.GetStatus());
        Assert.Equal(ourFolder, update.OurFolder);
        Assert.Equal(backupFolder, update.BackupFolder);
    }

    [Fact]
    public void Apply_gives_the_process_path_the_same_replacement()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
            ProcessPath = @"C:\Windows;C:\OldHistoria;C:\Tools",
        };
        files.Add(@"C:\OldHistoria\historia-cmdr.exe");
        files.Add(@"C:\OldHistoria\Historia.exe");
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;
        var result = service.Apply(preview);

        Assert.IsType<CmdrPathApplyResult.Written>(result);
        Assert.Equal(@"C:\Windows;C:\HistoriaApp;C:\Tools", store.ProcessPath);
    }

    [Fact]
    public void Apply_reports_Changed_when_an_entry_appears_that_the_same_result_would_remove()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
            @"C:\Old\historia-cmdr.exe",
            @"C:\Old\Historia.exe",
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;
        Assert.Empty(preview.Removed);

        // Replacing C:\Old in place gives the same new value the preview showed.
        store.UserPath = @"C:\Tools;C:\Old";

        var result = service.Apply(preview);
        Assert.IsType<CmdrPathApplyResult.Changed>(result);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void Apply_reports_Changed_when_a_folder_gains_historia_cmdr_after_preview()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools;C:\Old",
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;

        files.Add(@"C:\Old\historia-cmdr.exe");
        files.Add(@"C:\Old\Historia.exe");

        var result = service.Apply(preview);
        Assert.IsType<CmdrPathApplyResult.Changed>(result);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void Our_own_folder_on_the_machine_path_is_not_a_machine_copy()
    {
        var ourFolder = @"C:\HistoriaApp";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(ourFolder, "historia-cmdr.exe"),
            Path.Combine(ourFolder, "Historia.exe"),
        };
        var store = new FakeEnvironmentStore
        {
            UserPath = @"C:\Tools",
            MachinePath = @"C:\Windows;C:\HistoriaApp\",
        };
        var service = new CmdrPathService(
            store,
            ourFolder,
            files.Contains,
            @"C:\Backups",
            (folder, content) => Path.Combine(folder, "backup.reg"),
            AppLog.Current);

        var status = Assert.IsType<CmdrPathStatus.NeedsUpdate>(service.GetStatus());
        Assert.Empty(status.MachineCopies);
    }
}
