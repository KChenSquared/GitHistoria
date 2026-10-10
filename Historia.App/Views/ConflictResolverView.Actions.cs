namespace Historia.Views;

public sealed partial class ConflictResolverView
{
    public void GoToNextConflict()
    {
        var regions = NavigableRegions();
        if (regions.Count == 0)
        {
            return;
        }

        int currentLine = _resultEditor.TextArea.Caret.Line - 1;
        var next = regions.FirstOrDefault(r => r.Start > currentLine, regions[0]);
        _resultEditor.ScrollToLine(next.Start + 1);
        _resultEditor.TextArea.Caret.Line = next.Start + 1;
    }

    public void GoToPreviousConflict()
    {
        var regions = NavigableRegions();
        if (regions.Count == 0)
        {
            return;
        }

        int currentLine = _resultEditor.TextArea.Caret.Line - 1;
        var prev = regions.LastOrDefault(r => r.End < currentLine, regions[^1]);
        _resultEditor.ScrollToLine(prev.Start + 1);
        _resultEditor.TextArea.Caret.Line = prev.Start + 1;
    }

    public async Task<bool> SaveCurrentFileAsync(bool stage)
    {
        if (_isBusy || !_isEditable || _currentFile == null || _workingCopy == null || _workingCopy.Format is null)
        {
            return false;
        }

        var savedVersion = _resultEditor.Document.Version;
        var textToSave = _session.ComposeOutput();

        _saving = true;
        IsBusy = true;
        try
        {
            var res = await _conflicts.SaveResolutionAsync(
                _ctx,
                _currentFile.FilePath,
                textToSave,
                _workingCopy.Format.Value,
                _diskHash ?? string.Empty,
                stage,
                _viewCts.Token);

            if (!res.Saved)
            {
                _changedOnDiskBanner.IsVisible = true;
                return false;
            }

            _diskHash = res.DiskHash;
            _changedOnDiskBanner.IsVisible = false;

            if (res.StageError != null)
            {
                _notifications.Publish(NotificationSeverity.Error, $"Saved {_currentFile.FilePath}, but could not mark it resolved: {res.StageError.Message}", res.StageError.NotificationDetails());
                return false;
            }

            bool versionUnchanged = _resultEditor.Document.Version.CompareAge(savedVersion) == 0;
            if (versionUnchanged)
            {
                _resultEditor.Document.UndoStack.MarkAsOriginalFile();
                UnregisterUnsavedWork();
            }

            if (stage && versionUnchanged)
            {
                await RefreshConflictsAsync();
                if (_conflictedFiles.Count > 0)
                {
                    var next = _conflictedFiles.FirstOrDefault(f => !string.Equals(f.FilePath, _currentFile.FilePath, StringComparison.Ordinal))
                               ?? _conflictedFiles[0];
                    await SwitchToFileAsync(next);
                }
                else
                {
                    ShowAllConflictsResolvedCard();
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Save"), ex.NotificationDetails());
            return false;
        }
        finally
        {
            _saving = false;
            IsBusy = false;
        }
    }

    public async Task SaveAndMarkResolvedAsync()
    {
        if (_isBusy)
        {
            return;
        }

        if (!await ConfirmMarkResolvedAsync())
        {
            return;
        }

        await SaveCurrentFileAsync(stage: true);
    }

    /// <summary>
    /// Before marking a file resolved: warn about conflict blocks still in it, and about managed
    /// conflicts that keep no line and will save as nothing. Neither is destructive — the text is
    /// what the user sees, and it stays editable after staging.
    /// </summary>
    private async Task<bool> ConfirmMarkResolvedAsync()
    {
        int remaining = (_parse?.Regions.Count ?? 0) + (_parse?.MalformedAt.Count ?? 0);
        int emptied = _session.EmptyRegionCount;
        if (remaining > 0)
        {
            string message = $"{remaining} conflict block(s) remain — mark resolved anyway?";
            if (emptied > 0)
            {
                message += $" {emptied} conflict(s) will save as no lines.";
            }
            return await _dialogs.ConfirmAsync("Unresolved conflicts", message, confirmLabel: "Mark resolved");
        }

        if (emptied > 0)
        {
            return await _dialogs.ConfirmAsync(
                "Emptied conflicts",
                $"{emptied} conflict(s) will save as no lines — mark resolved anyway?",
                confirmLabel: "Mark resolved");
        }

        return true;
    }

    private async Task TakeSideAsync(ConflictSide side)
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        var path = _currentFile.FilePath;
        IsBusy = true;
        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            bool isAbsent = side == ConflictSide.Ours
                ? (_currentFile.Status is ConflictStatus.DeletedByUs or ConflictStatus.AddedByThem or ConflictStatus.BothDeletedRenamed)
                : (_currentFile.Status is ConflictStatus.DeletedByThem or ConflictStatus.AddedByUs or ConflictStatus.BothDeletedRenamed);
            var sideTitle = side == ConflictSide.Ours ? labels.OursTitle : labels.TheirsTitle;
            string title = isAbsent ? $"Delete {path}" : (side == ConflictSide.Ours ? "Take ours" : "Take theirs");
            string message = isAbsent
                ? $"Delete {path} — {sideTitle} deleted it. Any edits to this file will be lost."
                : $"Replace {path} with {sideTitle}'s version? Any edits to this file will be lost.";
            string verb = isAbsent ? "Delete" : (side == ConflictSide.Ours ? "Take ours" : "Take theirs");

            if (!await _dialogs.ConfirmDestructiveAsync(title, message, verb))
            {
                return;
            }

            await _conflicts.ResolveWithSideAsync(_ctx, path, side, _viewCts.Token);
            UnregisterUnsavedWork();
            await RefreshConflictsAsync();
            if (_conflictedFiles.Count > 0)
            {
                var next = _conflictedFiles.FirstOrDefault(f => !string.Equals(f.FilePath, path, StringComparison.Ordinal))
                           ?? _conflictedFiles[0];
                await SwitchToFileAsync(next);
            }
            else
            {
                ShowAllConflictsResolvedCard();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage($"Take {(side == ConflictSide.Ours ? "ours" : "theirs")}"), ex.NotificationDetails());
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task TakeOursCurrentAsync() => TakeSideAsync(ConflictSide.Ours);

    public Task TakeTheirsCurrentAsync() => TakeSideAsync(ConflictSide.Theirs);

    public async Task RecreateConflictAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        var path = _currentFile.FilePath;
        IsBusy = true;
        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Reset file to conflicted",
                    $"Rewrite {path} back to its conflicted state, including the common-ancestor (base) section? Any edits to this file will be lost.",
                    "Reset"))
            {
                return;
            }

            await _conflicts.RecreateConflictAsync(_ctx, path, _viewCts.Token);
            UnregisterUnsavedWork();
            await RefreshConflictsAsync();
            var target = _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, path, StringComparison.Ordinal));
            if (target != null)
            {
                await SwitchToFileAsync(target);
            }
            else if (_conflictedFiles.Count > 0)
            {
                await SwitchToFileAsync(_conflictedFiles[0]);
            }
            else
            {
                ShowAllConflictsResolvedCard();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Reset file to conflicted"), ex.NotificationDetails());
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LaunchExternalToolAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        try
        {
            if (HasUnsavedWork)
            {
                var work = _unsavedRegistry?.For(_ctx) ?? [new ConflictUnsavedWork(this, _ctx, _currentFile.FilePath)];
                var proceed = await UnsavedWorkPrompt.ResolveAsync(work, _dialogs);
                if (!proceed)
                {
                    return;
                }
            }

            IsBusy = true;
            try
            {
                var launched = await _conflicts.LaunchExternalMergeToolAsync(_ctx, _currentFile.FilePath, _viewCts.Token);
                if (!launched)
                {
                    var settings = _sp.GetService(typeof(ISettingsService)) as ISettingsService;
                    var tool = settings?.Current.ExternalMergeTool;
                    if (!string.IsNullOrWhiteSpace(tool))
                    {
                        _notifications.Publish(NotificationSeverity.Warning, $"The merge tool exited with conflict markers still in {_currentFile.FilePath}.");
                    }
                    else
                    {
                        _notifications.Publish(NotificationSeverity.Warning, "No external merge tool is configured (git config merge.tool).");
                    }
                    return;
                }

                UnregisterUnsavedWork();
                await RefreshConflictsAsync();
                var target = _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, _currentFile.FilePath, StringComparison.Ordinal));
                if (target != null)
                {
                    await SwitchToFileAsync(target);
                }
                else if (_conflictedFiles.Count > 0)
                {
                    await SwitchToFileAsync(_conflictedFiles[0]);
                }
                else
                {
                    ShowAllConflictsResolvedCard();
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("External tool"), ex.NotificationDetails());
        }
    }

    public async Task ContinueAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            if (HasUnsavedWork && _currentFile != null)
            {
                var choice = await UnsavedWorkPrompt.ResolveAsync(
                    _dialogs,
                    "Unsaved changes",
                    $"{_currentFile.FilePath} has unsaved edits. Save and mark it resolved before continuing?",
                    "Save & mark resolved",
                    "Discard changes");

                switch (choice)
                {
                    case UnsavedWorkChoice.Save:
                        if (!await ConfirmMarkResolvedAsync())
                        {
                            return;
                        }

                        var saved = await SaveCurrentFileAsync(stage: true);
                        if (!saved)
                        {
                            return;
                        }
                        break;

                    case UnsavedWorkChoice.Discard:
                        DiscardUnsavedState();
                        await SwitchToFileAsync(_currentFile);
                        break;

                    case UnsavedWorkChoice.Cancel:
                    default:
                        return;
                }
            }

            IsBusy = true;
            try
            {
                await _conflicts.ContinueOperationAsync(_ctx, _ctx.Lifetime);
                if (_ctx.State.PendingOperation == OperationState.None)
                {
                    _ctx.State.SetKeepConflictResolverOpen(false);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.MergeConflict)
        {
            _notifications.Publish(NotificationSeverity.Warning, ex.Message);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Continue"), ex.NotificationDetails());
        }
    }

    public async Task SkipAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            var commitInfo = string.IsNullOrWhiteSpace(labels.TheirsDetail) ? "the current commit" : labels.TheirsDetail;
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Skip commit",
                    $"Skip {commitInfo}? Resolutions and unsaved edits in this stop will be discarded.",
                    "Skip"))
            {
                return;
            }

            IsBusy = true;
            try
            {
                await _conflicts.SkipCurrentAsync(_ctx, _ctx.Lifetime);
                DiscardUnsavedState();
                _changedOnDiskBanner.IsVisible = false;
                if (_ctx.State.PendingOperation == OperationState.None)
                {
                    _ctx.State.SetKeepConflictResolverOpen(false);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Skip"), ex.NotificationDetails());
        }
    }

    public async Task AbortAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Abort operation",
                    "Abort the in-progress merge / rebase / cherry-pick? Conflict resolutions and unsaved edits made so far will be discarded.",
                    "Abort"))
            {
                return;
            }

            IsBusy = true;
            try
            {
                await _conflicts.AbortOperationAsync(_ctx, _ctx.Lifetime);
                DiscardUnsavedState();
                _changedOnDiskBanner.IsVisible = false;
                if (_ctx.State.PendingOperation == OperationState.None)
                {
                    _ctx.State.SetKeepConflictResolverOpen(false);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Abort"), ex.NotificationDetails());
        }
    }

    private async Task CheckExternalDiskChangeAsync()
    {
        if (_disposed || _saving || _currentFile == null || _diskHash == null)
        {
            return;
        }

        string currentHash;
        try
        {
            currentHash = await _conflicts.GetDiskHashAsync(_ctx, _currentFile.FilePath, _viewCts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable right now (locked mid-save, scanned): the next status change checks again.
            return;
        }

        if (_disposed) return;
        if (currentHash != _diskHash)
        {
            if (!HasUnsavedWork)
            {
                await SwitchToFileAsync(_currentFile);
            }
            else
            {
                _changedOnDiskBanner.IsVisible = true;
            }
        }
    }

    private async Task ReloadFromDiskAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Reload from disk",
                    $"Reload {_currentFile.FilePath} from disk? Your unsaved edits will be discarded.",
                    "Reload"))
            {
                return;
            }

            _changedOnDiskBanner.IsVisible = false;
            UnregisterUnsavedWork();
            await SwitchToFileAsync(_currentFile);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Reload"), ex.NotificationDetails());
        }
    }

    internal async Task KeepMineAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        var path = _currentFile.FilePath;
        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Keep my version",
                    $"Your next save will overwrite {path} on disk, which changed outside Historia.",
                    "Keep mine"))
            {
                return;
            }

            _changedOnDiskBanner.IsVisible = false;
            IsBusy = true;
            try
            {
                var copy = await _conflicts.LoadWorkingCopyAsync(_ctx, path, _ctx.Lifetime);
                _diskHash = copy.DiskHash;
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Keep mine"), ex.NotificationDetails());
        }
    }

    private void RegisterUnsavedWork()
    {
        if (_unsavedRegistration != null || _unsavedRegistry == null || _currentFile == null)
        {
            return;
        }

        _unsavedRegistration = _unsavedRegistry.Register(new ConflictUnsavedWork(this, _ctx, _currentFile.FilePath));
    }

    private void UnregisterUnsavedWork()
    {
        _unsavedRegistration?.Dispose();
        _unsavedRegistration = null;
    }

    private void DiscardUnsavedState()
    {
        UnregisterUnsavedWork();
        _resultEditor.Document.UndoStack.MarkAsOriginalFile();
    }
}
