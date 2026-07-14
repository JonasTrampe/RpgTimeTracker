using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using RpgTimeTracker.Models;
using RpgTimeTracker.Models.Persistence;
using RpgTimeTracker.Network;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Network;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Models.Theming;
using RpgTimeTracker.Shared.Services;
using RpgTimeTracker.Shared.Services.Localization;
using RpgTimeTracker.Shared.Services.Theming;
using RpgTimeTracker.Shared.Services.Visuals;
using RpgTimeTracker.Shared.ViewModels;
using Serilog;

namespace RpgTimeTracker.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IPlayerDisplayContext
{
    // ==================== Characters (NPC) library ====================
    // Own top-level library, not nested under Maps - see NpcLibraryItemViewModel's doc comment
    // for why it doesn't derive from LibraryItemViewModelBase<TSelf> like Media/Sound.

    public ObservableCollection<NpcLibraryItemViewModel> NpcLibrary { get; } = [];

    public bool HasNoNpcLibraryItems => NpcLibrary.Count == 0;

    [ObservableProperty] private NpcLibraryItemViewModel? _selectedNpc;

    [RelayCommand]
    private async Task AddNpcAsync()
    {
        var scope = await ResolveScopeForNewItemAsync();
        var npc = new NpcLibraryItemViewModel(Guid.NewGuid(),
            LocalizationService.Get("MainWindowViewModel.Defaults.NewNpcName"), RemoveNpc,
            OnNpcLibraryItemChanged, scope);
        npc.AddVariant(LocalizationService.Get("MainWindowViewModel.Defaults.DefaultVariantName"), isDefault: true);
        NpcLibrary.Add(npc);
        SelectedNpc = npc;
        OnPropertyChanged(nameof(HasNoNpcLibraryItems));
        SaveNpcLibrarySettings();
    }

    private void RemoveNpc(NpcLibraryItemViewModel npc)
    {
        NpcLibrary.Remove(npc);
        if (SelectedNpc == npc) SelectedNpc = null;
        OnPropertyChanged(nameof(HasNoNpcLibraryItems));
        SaveNpcLibrarySettings();
    }

    private void OnNpcLibraryItemChanged(NpcLibraryItemViewModel npc)
    {
        SaveNpcLibrarySettings();
    }

    /// <summary>See MoveMediaLibraryItemToScope's doc comment - Characters own no files (see
    ///     NpcLibraryItemViewModel's doc comment), so moving scope is just a Scope flip + re-save,
    ///     no file to relocate.</summary>
    public void MoveNpcLibraryItemToScope(NpcLibraryItemViewModel npc, LibraryScope targetScope) =>
        MoveLibraryItemToScope(npc.Scope, targetScope, "Character", npc.Name, () =>
        {
            npc.Scope = targetScope;
            SaveNpcLibrarySettings();
        });

    /// <summary>See MoveMediaLibraryItemToShared/ToSession's doc comment - same wrapping for Characters.</summary>
    [RelayCommand]
    private void MoveNpcLibraryItemToShared(NpcLibraryItemViewModel npc) =>
        MoveNpcLibraryItemToScope(npc, LibraryScope.Shared);

    [RelayCommand]
    private void MoveNpcLibraryItemToSession(NpcLibraryItemViewModel npc) =>
        MoveNpcLibraryItemToScope(npc, LibraryScope.SessionLocal);

    /// <summary>Who references a Media/Sound Library item by Id from any NPC variant's Image/
    ///     TokenImage/Sounds - registered into _usageRegistry so deleting that Media/Sound item
    ///     goes through the same 3-way confirm-delete flow as any other in-use item.</summary>
    private IEnumerable<string> FindNpcUsagesById(Guid id)
    {
        foreach (var npc in NpcLibrary)
        foreach (var variant in npc.Variants)
        {
            if (variant.Image?.Id == id || variant.TokenImage?.Id == id || variant.Sounds.Any(s => s.Id == id))
                yield return string.Format(LocalizationService.Get("MainWindowViewModel.Labels.NpcUsage"),
                    npc.Name, variant.Name);
        }
    }

    private void ClearNpcReferencesById(Guid id)
    {
        foreach (var npc in NpcLibrary)
        foreach (var variant in npc.Variants)
        {
            if (variant.Image?.Id == id) variant.Image = null;
            if (variant.TokenImage?.Id == id) variant.TokenImage = null;

            var staleSounds = variant.Sounds.Where(s => s.Id == id).ToList();
            foreach (var sound in staleSounds) variant.RemoveSound(sound);
        }
    }

    private static NpcLibraryEntryDto ToNpcLibraryEntryDto(NpcLibraryItemViewModel npc) => new()
    {
        Id = npc.Id,
        Name = npc.Name,
        GmInfoBlocks = npc.GmInfoBlocks.Select(b => new NpcGmInfoBlockDto
        {
            Title = b.Title, MarkdownBody = b.MarkdownBody
        }).ToList(),
        Variants = npc.Variants.Select(v => new NpcVariantEntryDto
        {
            Id = v.Id,
            Name = v.Name,
            IsDefault = v.IsDefault,
            ImageId = v.Image?.Id,
            TokenImageId = v.TokenImage?.Id,
            TokenIcon = v.TokenIcon,
            PlayerInfo = v.PlayerInfo,
            SoundIds = v.HasSoundsOverride ? v.Sounds.Select(sound => sound.Id).ToList() : null
        }).ToList(),
        ActiveVariantId = npc.ActiveVariant?.Id ?? npc.DefaultVariant.Id
    };

    /// <summary>Wrapped in BeginBulkLoad/EndBulkLoad - this NPC isn't in the NpcLibrary collection
    ///     yet while this method builds it, so every AddVariant/property-set call below would
    ///     otherwise fire a save that serializes NpcLibrary as it stood before this (and any later)
    ///     entry was added, silently truncating the on-disk library. See
    ///     NpcLibraryItemViewModel._suppressChangeNotifications' doc comment.</summary>
    private NpcLibraryItemViewModel? FromNpcLibraryEntryDto(NpcLibraryEntryDto entry, LibraryScope scope)
    {
        var npc = new NpcLibraryItemViewModel(entry.Id, entry.Name, RemoveNpc, OnNpcLibraryItemChanged, scope);
        npc.BeginBulkLoad();
        try
        {
            foreach (var block in entry.GmInfoBlocks)
                npc.GmInfoBlocks.Add(new NpcGmInfoBlockViewModel(block.Title, block.MarkdownBody,
                    b => { npc.GmInfoBlocks.Remove(b); OnNpcLibraryItemChanged(npc); }, _ => OnNpcLibraryItemChanged(npc)));

            foreach (var variantEntry in entry.Variants)
            {
                var variant = npc.AddVariant(variantEntry.Name, variantEntry.IsDefault, variantEntry.Id);
                variant.Image = variantEntry.ImageId is { } imageId
                    ? MediaLibrary.FirstOrDefault(m => m.Id == imageId)
                    : null;
                variant.TokenImage = variantEntry.TokenImageId is { } tokenImageId
                    ? MediaLibrary.FirstOrDefault(m => m.Id == tokenImageId)
                    : null;
                variant.TokenIcon = variantEntry.TokenIcon;
                variant.PlayerInfo = variantEntry.PlayerInfo;
                // Empty PlayerInfo starts in edit mode (nothing to preview); non-empty PlayerInfo
                // loaded from disk starts in preview - see IsPlayerInfoPreviewMode's doc comment.
                variant.IsPlayerInfoPreviewMode = !string.IsNullOrWhiteSpace(variantEntry.PlayerInfo);
                if (variantEntry.SoundIds is { } soundIds)
                {
                    foreach (var soundId in soundIds)
                    {
                        var sound = SoundLibrary.FirstOrDefault(s => s.Id == soundId);
                        if (sound is not null) variant.Sounds.Add(sound);
                    }

                    variant.HasSoundsOverride = true;
                }
            }

            if (npc.Variants.Count == 0) return null; // a Default variant is required - drop a malformed entry

            npc.ActiveVariant = npc.Variants.FirstOrDefault(v => v.Id == entry.ActiveVariantId) ?? npc.DefaultVariant;
            return npc;
        }
        finally
        {
            npc.EndBulkLoad();
        }
    }

    /// <summary>See SaveMediaLibrarySettings' doc comment - same Shared/SessionLocal split.</summary>
    private void SaveNpcLibrarySettings() =>
        SaveLibrarySettings(NpcLibrary, n => n.Scope, ToNpcLibraryEntryDto,
            (settings, list) => settings.NpcLibrary = list,
            (sessionLibrary, list) => sessionLibrary.NpcLibrary = list);

}
