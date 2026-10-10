namespace Historia.Tests;

[SupportedOSPlatform("windows")]
public sealed class CmdrPathServiceTests
{
    private sealed class FakeEnvironmentStore : IUserEnvironmentStore
    {
        public string? UserPath { get; set; }
        public RegistryValueKind UserKind { get; set; } = RegistryValueKind.ExpandString;
        public string? MachinePath { get; set; }
        public int BroadcastCount { get; private set; }
        public List<(string Value, RegistryValueKind Kind)> Writes { get; } = [];
        public bool ThrowOnWrite { get; set; }

        public (string? Value, RegistryValueKind Kind) ReadUserPath() => (UserPath, UserKind);

        public string? ReadMachinePath() => MachinePath;

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
            _ => {});

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
            _ => {});

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
            _ => {});

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
            _ => {});

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
            _ => {});

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
            _ => {});

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
            _ => {});

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
            _ => {});

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
            _ => {});

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
            _ => {});

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
            _ => {});

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
            _ => {});

        var preview = ((CmdrPathStatus.NeedsUpdate)service.GetStatus()).Preview;
        var result = service.Apply(preview);

        var written = Assert.IsType<CmdrPathApplyResult.Written>(result);
        Assert.Equal(expectedPath, written.BackupPath);
    }
}
