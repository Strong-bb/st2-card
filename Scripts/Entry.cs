using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;

namespace STS2DeckTools.Scripts;

[ModInitializer("Init")]
public partial class Entry
{
    private static bool _selectionUiBusy;
    private const string BackButtonScenePath = "res://scenes/ui/back_button.tscn";
    private const string AddCardButtonName = "AddCard";
    private const string RemoveCardButtonName = "RemoveCard";
    private const string AddCardLabel = "Add Card";
    private const string RemoveCardLabel = "Remove Card";

    public static void Init()
    {
        var harmony = new Harmony("sts2decktools.devconsole");
        harmony.PatchAll(typeof(Entry).Assembly);
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);
        Log.Debug("STS2DeckTools initialized.");
    }

    [HarmonyPatch(typeof(DevConsole))]
    private static class DevConsolePatch
    {
        private static readonly System.Reflection.MethodInfo? RegisterCommandMethod = AccessTools.Method(typeof(DevConsole), "RegisterCommand");

        [HarmonyPostfix]
        [HarmonyPatch(MethodType.Constructor)]
        [HarmonyPatch(new[] { typeof(bool) })]
        private static void RegisterCardCommands(DevConsole __instance)
        {
            if (RegisterCommandMethod == null)
            {
                Log.Error("STS2DeckTools could not find DevConsole.RegisterCommand.");
                return;
            }

            RegisterCommandMethod.Invoke(__instance, new object[] { new SelectCardConsoleCmd() });
            RegisterCommandMethod.Invoke(__instance, new object[] { new RemoveCardConsoleCmd() });
        }
    }

    [HarmonyPatch(typeof(NPauseMenu))]
    private static class PauseMenuPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(NPauseMenu._Ready))]
        private static void InjectCardButtons(NPauseMenu __instance)
        {
            if (!RunManager.Instance.IsInProgress)
            {
                return;
            }

            Control buttonContainer = __instance.GetNode<Control>("%ButtonContainer");
            string templateName = RunManager.Instance.NetService.Type == NetGameType.Client ? "Disconnect" : "SaveAndQuit";
            NPauseMenuButton templateButton = buttonContainer.GetNode<NPauseMenuButton>(templateName);

            EnsurePauseMenuButton(
                buttonContainer,
                templateButton,
                AddCardButtonName,
                AddCardLabel,
                () => TaskHelper.RunSafely(OpenSelectionFromPauseMenu(SelectionMode.AddToDeck)));

            NPauseMenuButton removeCardButton = EnsurePauseMenuButton(
                buttonContainer,
                templateButton,
                RemoveCardButtonName,
                RemoveCardLabel,
                () => TaskHelper.RunSafely(OpenSelectionFromPauseMenu(SelectionMode.RemoveFromDeck)));
            ConfigureRemoveCardButton(removeCardButton);

            WirePauseMenuFocus(buttonContainer);
        }
    }

    private sealed class SelectCardConsoleCmd : AbstractConsoleCmd
    {
        public override string CmdName => "selectcard";

        public override string Args => "[open]";

        public override string Description => "Opens an in-game card selection panel for the current character and adds the selected card directly to the deck.";

        public override bool IsNetworked => true;

        public override bool DebugOnly => false;

        public override CmdResult Process(Player? issuingPlayer, string[] args)
        {
            if (!RunManager.Instance.IsInProgress)
            {
                return new CmdResult(success: false, "A run is currently not in progress.");
            }

            if (issuingPlayer == null)
            {
                return new CmdResult(success: false, "No active player was found for this command.");
            }

            if (args.Length == 0)
            {
                return BuildOpenResult(issuingPlayer);
            }

            string subCommand = args[0].Trim().ToLowerInvariant();
            return subCommand switch
            {
                "open" => BuildOpenResult(issuingPlayer),
                _ => new CmdResult(success: false, "Usage: selectcard or selectcard open")
            };
        }

        public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
        {
            if (args.Length <= 1)
            {
                return CompleteArgument(new[] { "open" }, Array.Empty<string>(), args.FirstOrDefault() ?? "");
            }

            return base.GetArgumentCompletions(player, args);
        }

        private static CmdResult BuildOpenResult(Player player)
        {
            if (!TryBeginSelectionSession())
            {
                return new CmdResult(success: false, "The card selection panel is already open.");
            }

            IReadOnlyList<CardChoice> choices = GetChoices(player);
            if (choices.Count == 0)
            {
                EndSelectionSession();
                return new CmdResult(success: false, "No available cards were found for the current character.");
            }

            Task task = OpenSelectionPanel(player, choices, SelectionMode.AddToDeck);
            return new CmdResult(task, success: true, $"Opened selection panel with {choices.Count} cards for {player.Character.Title.GetFormattedText()}.");
        }

        internal static async Task OpenSelectionPanel(Player player, IReadOnlyList<CardChoice> choices, SelectionMode mode)
        {
            List<CardModel> cardsForSelection = mode == SelectionMode.AddToDeck
                ? choices.Select(choice => player.RunState.CreateCard(choice.CanonicalCard, player)).ToList()
                : choices.Select(choice => choice.CanonicalCard).ToList();

            try
            {
                CardSelectorPrefs prefs = new CardSelectorPrefs(new LocString("gameplay_ui", "CHOOSE_CARD_HEADER"), 1);
                NSimpleCardSelectScreen selectionScreen = NSimpleCardSelectScreen.Create(cardsForSelection, prefs);
                NOverlayStack.Instance?.Push(selectionScreen);
                await selectionScreen.ToSignal(selectionScreen.GetTree(), SceneTree.SignalName.ProcessFrame);
                AddCancelButton(selectionScreen);
                selectionScreen.GetNode("%BottomLabel").Call("SetTextAutoSize", mode.BottomLabelText);

                CardModel? selectedCard = (await selectionScreen.CardsSelected()).FirstOrDefault();
                if (selectedCard == null)
                {
                    Log.Info($"STS2DeckTools {mode.LogActionName} selection was canceled.");
                    return;
                }

                if (mode == SelectionMode.AddToDeck)
                {
                    CardPileAddResult result = await CardPileCmd.Add(selectedCard, PileType.Deck);
                    if (!result.success)
                    {
                        Log.Warn($"STS2DeckTools failed to add '{selectedCard.Title}' [{selectedCard.Id.Entry}] to the deck.");
                        return;
                    }

                    Log.Info($"STS2DeckTools added '{result.cardAdded.Title}' [{result.cardAdded.Id.Entry}] to the deck from the selection panel.");
                }
                else
                {
                    bool removed = await RunNativeRemoval(player);
                    if (!removed)
                    {
                        Log.Warn("STS2DeckTools native remove-card flow failed or was canceled.");
                        return;
                    }

                    Log.Info("STS2DeckTools removed a card using the native flow from the selection panel.");
                }
            }
            catch (TaskCanceledException)
            {
                Log.Info($"STS2DeckTools {mode.LogActionName} selection panel was closed before a card was chosen.");
            }
            finally
            {
                if (mode == SelectionMode.AddToDeck)
                {
                    foreach (CardModel card in cardsForSelection.Where(card => card.Pile == null && card.Owner != null && player.RunState.ContainsCard(card)))
                    {
                        player.RunState.RemoveCard(card);
                    }
                }

                EndSelectionSession();
            }
        }

        internal static IReadOnlyList<CardChoice> GetChoices(Player player)
        {
            IReadOnlyDictionary<string, string> localizedTitles = CardTitleCache.GetTitles();
            return player.Character.CardPool
                .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
                .GroupBy(card => card.Id.Entry, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    string cardId = group.Key;
                    string displayName = localizedTitles.TryGetValue(cardId, out string? title) && !string.IsNullOrWhiteSpace(title)
                        ? title
                        : group.First().TitleLocString.GetRawText();
                    return new CardChoice(cardId, displayName, group.First());
                })
                .OrderBy(choice => GetRaritySortOrder(choice.CanonicalCard.Rarity))
                .ThenBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(choice => choice.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int GetRaritySortOrder(CardRarity rarity)
        {
            return rarity switch
            {
                CardRarity.Basic => 0,
                CardRarity.Common => 1,
                CardRarity.Uncommon => 2,
                CardRarity.Rare => 3,
                CardRarity.Status => 4,
                CardRarity.Curse => 5,
                CardRarity.Event => 6,
                CardRarity.Token => 7,
                CardRarity.Quest => 8,
                CardRarity.Ancient => 9,
                _ => int.MaxValue
            };
        }
    }

    private sealed class RemoveCardConsoleCmd : AbstractConsoleCmd
    {
        public override string CmdName => "removecard";

        public override string Args => "[open]";

        public override string Description => "Opens an in-game card selection panel for the current deck and removes the selected card.";

        public override bool IsNetworked => true;

        public override bool DebugOnly => false;

        public override CmdResult Process(Player? issuingPlayer, string[] args)
        {
            if (!RunManager.Instance.IsInProgress)
            {
                return new CmdResult(success: false, "A run is currently not in progress.");
            }

            if (issuingPlayer == null)
            {
                return new CmdResult(success: false, "No active player was found for this command.");
            }

            if (args.Length == 0)
            {
                return BuildOpenResult(issuingPlayer);
            }

            string subCommand = args[0].Trim().ToLowerInvariant();
            return subCommand switch
            {
                "open" => BuildOpenResult(issuingPlayer),
                _ => new CmdResult(success: false, "Usage: removecard or removecard open")
            };
        }

        public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
        {
            if (args.Length <= 1)
            {
                return CompleteArgument(new[] { "open" }, Array.Empty<string>(), args.FirstOrDefault() ?? "");
            }

            return base.GetArgumentCompletions(player, args);
        }

        private static CmdResult BuildOpenResult(Player player)
        {
            if (!TryBeginSelectionSession())
            {
                return new CmdResult(success: false, "The card selection panel is already open.");
            }

            Task task = OpenNativeRemovalPanel(player);
            return new CmdResult(task, success: true, $"Opened native deck removal panel for {player.Character.Title.GetFormattedText()}.");
        }
    }

    private sealed record CardChoice(string Id, string DisplayName, CardModel CanonicalCard);

    private sealed record SelectionMode(string BottomLabelText, string LogActionName)
    {
        public static readonly SelectionMode AddToDeck = new("Choose a card to add to the deck", "add-card");
        public static readonly SelectionMode RemoveFromDeck = new("Choose a card to remove from the deck", "remove-card");
    }

    private static class CardTitleCache
    {
        private const string CardsLocPath = "res://localization/zhs/cards.json";
        private static Dictionary<string, string>? _titles;

        public static IReadOnlyDictionary<string, string> GetTitles()
        {
            return _titles ??= LoadTitles();
        }

        private static Dictionary<string, string> LoadTitles()
        {
            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using Godot.FileAccess? file = Godot.FileAccess.Open(CardsLocPath, Godot.FileAccess.ModeFlags.Read);
            if (file == null)
            {
                Log.Warn($"STS2DeckTools could not open localization file at '{CardsLocPath}'.");
                return titles;
            }

            string json = file.GetAsText();
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (!property.Name.EndsWith(".title", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string cardId = property.Name[..^".title".Length];
                    string title = property.Value.GetString() ?? cardId;
                    titles[cardId.ToUpperInvariant()] = title;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"STS2DeckTools failed to parse '{CardsLocPath}': {ex}");
            }

            return titles;
        }
    }

    private static void WirePauseMenuFocus(Control buttonContainer)
    {
        List<NPauseMenuButton> buttons = buttonContainer.GetChildren()
            .OfType<NPauseMenuButton>()
            .Where(button => button.Visible)
            .ToList();

        for (int i = 0; i < buttons.Count; i++)
        {
            NPauseMenuButton button = buttons[i];
            button.FocusNeighborLeft = button.GetPath();
            button.FocusNeighborRight = button.GetPath();
            button.FocusNeighborTop = i > 0 ? buttons[i - 1].GetPath() : button.GetPath();
            button.FocusNeighborBottom = i < buttons.Count - 1 ? buttons[i + 1].GetPath() : button.GetPath();
        }
    }

    private static async Task OpenSelectionFromPauseMenu(SelectionMode mode)
    {
        if (!TryBeginSelectionSession())
        {
            return;
        }

        NCapstoneContainer.Instance?.Close();
        CloseMapIfOpen();
        NRun.Instance?.GlobalUi?.TopBar?.Pause?.ToggleAnimState();
        if (NGame.Instance == null)
        {
            Log.Error("STS2DeckTools could not access NGame while opening from the pause menu.");
            EndSelectionSession();
            return;
        }

        // Let the pause screen fully animate out before pushing another overlay.
        await NGame.Instance.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);
        await NGame.Instance.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);
        await Task.Delay(75);

        Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        if (player == null)
        {
            Log.Error("STS2DeckTools could not find the local player when opening from the pause menu.");
            EndSelectionSession();
            return;
        }

        if (!CanOpenFromCurrentState(mode, out string? reason))
        {
            Log.Warn($"STS2DeckTools blocked {mode.LogActionName}: {reason}");
            EndSelectionSession();
            return;
        }

        if (mode == SelectionMode.RemoveFromDeck)
        {
            await OpenNativeRemovalPanel(player);
            return;
        }

        IReadOnlyList<CardChoice> choices = SelectCardConsoleCmd.GetChoices(player);
        if (choices.Count == 0)
        {
            Log.Warn($"STS2DeckTools found no available cards for {mode.LogActionName} while opening from the pause menu.");
            EndSelectionSession();
            return;
        }

        await SelectCardConsoleCmd.OpenSelectionPanel(player, choices, mode);
    }

    private static bool TryBeginSelectionSession()
    {
        if (_selectionUiBusy)
        {
            return false;
        }

        _selectionUiBusy = true;
        return true;
    }

    private static void EndSelectionSession()
    {
        _selectionUiBusy = false;
    }

    private static void CloseMapIfOpen()
    {
        NMapScreen? mapScreen = NMapScreen.Instance;
        if (mapScreen?.IsOpen ?? false)
        {
            mapScreen.Close(animateOut: false);
        }
    }

    private static void AddCancelButton(NSimpleCardSelectScreen selectionScreen)
    {
        if (selectionScreen.GetNodeOrNull<NBackButton>("AddCardCancel") != null)
        {
            return;
        }

        PackedScene? scene = ResourceLoader.Load<PackedScene>(BackButtonScenePath);
        if (scene == null)
        {
            Log.Warn($"STS2SelectCard could not load cancel button scene at '{BackButtonScenePath}'.");
            return;
        }

        NBackButton cancelButton = scene.Instantiate<NBackButton>();
        cancelButton.Name = "AddCardCancel";
        selectionScreen.AddChild(cancelButton);
        selectionScreen.MoveChild(cancelButton, selectionScreen.GetChildCount() - 1);
        cancelButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => NOverlayStack.Instance?.Remove(selectionScreen)));
        cancelButton.Enable();
    }

    private static NPauseMenuButton EnsurePauseMenuButton(Control buttonContainer, NPauseMenuButton templateButton, string buttonName, string label, Action onPressed)
    {
        NPauseMenuButton? existingButton = buttonContainer.GetNodeOrNull<NPauseMenuButton>(buttonName);
        if (existingButton != null)
        {
            existingButton.GetNode("Label").Call("SetTextAutoSize", label);
            return existingButton;
        }

        NPauseMenuButton button = (NPauseMenuButton)templateButton.Duplicate();
        button.Name = buttonName;
        button.Visible = true;
        button.GetNode("Label").Call("SetTextAutoSize", label);
        buttonContainer.AddChild(button);
        buttonContainer.MoveChild(button, buttonContainer.GetChildCount() - 1);
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => onPressed()));
        return button;
    }

    private static void ConfigureRemoveCardButton(NPauseMenuButton button)
    {
        button.GetNode("Label").Call("SetTextAutoSize", RemoveCardLabel);
        button.Enable();
    }

    private static async Task OpenNativeRemovalPanel(Player player)
    {
        try
        {
            bool removed = await RunNativeRemoval(player);
            if (!removed)
            {
            Log.Info("STS2DeckTools native remove-card flow was canceled or found no removable cards.");
            }
        }
        finally
        {
            EndSelectionSession();
        }
    }

    private static Task<bool> RunNativeRemoval(Player player)
    {
        if (IsMerchantRoom())
        {
            Log.Info("STS2DeckTools using merchant native card-removal flow.");
            return RunManager.Instance.OneOffSynchronizer.DoLocalMerchantCardRemoval(0, cancelable: true);
        }

        Log.Info("STS2DeckTools using reward native card-removal flow.");
        return RunManager.Instance.RewardSynchronizer.DoLocalCardRemoval();
    }

    private static bool IsMerchantRoom()
    {
        object? currentRoom = GetCurrentRoom();
        string roomType = currentRoom?.GetType().FullName ?? string.Empty;
        return roomType.Contains("Merchant", StringComparison.OrdinalIgnoreCase);
    }

    private static object? GetCurrentRoom()
    {
        object? debugState = RunManager.Instance.DebugOnlyGetState();
        return GetReflectedMemberValue(RunManager.Instance, "CurrentRoom")
            ?? (debugState != null ? GetReflectedMemberValue(debugState, "CurrentRoom") : null)
            ?? TryGetCurrentRoomFromPlayers(debugState);
    }

    private static bool CanOpenFromCurrentState(SelectionMode mode, out string? reason)
    {
        reason = null;
        if (mode != SelectionMode.RemoveFromDeck)
        {
            return true;
        }

        if (!IsSafeForDeckMutation(out string roomType))
        {
            reason = $"current room '{roomType}' has its own progression flow";
            return false;
        }

        return true;
    }

    private static bool IsSafeForDeckMutation()
    {
        return IsSafeForDeckMutation(out _);
    }

    private static bool IsSafeForDeckMutation(out string roomType)
    {
        object? currentRoom = GetCurrentRoom();
        roomType = currentRoom?.GetType().FullName ?? string.Empty;
        return !roomType.Contains("Event", StringComparison.OrdinalIgnoreCase)
            && !roomType.Contains("Merchant", StringComparison.OrdinalIgnoreCase)
            && !roomType.Contains("Treasure", StringComparison.OrdinalIgnoreCase);
    }

    private static object? GetReflectedMemberValue(object target, string memberName)
    {
        Type type = target.GetType();
        System.Reflection.PropertyInfo? property = type.GetProperty(memberName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (property != null)
        {
            return property.GetValue(target);
        }

        System.Reflection.FieldInfo? field = type.GetField(memberName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(target);
    }

    private static object? TryGetCurrentRoomFromPlayers(object? playersState)
    {
        if (playersState is not System.Collections.IEnumerable enumerable)
        {
            return null;
        }

        foreach (object entry in enumerable)
        {
            object? runState = GetReflectedMemberValue(entry, "RunState");
            object? currentRoom = runState != null ? GetReflectedMemberValue(runState, "CurrentRoom") : null;
            if (currentRoom != null)
            {
                return currentRoom;
            }
        }

        return null;
    }
}
