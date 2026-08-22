using System.Collections.ObjectModel;
using System.ComponentModel;
using Cheatmaster.App.Infrastructure;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.ViewModels;

/// <summary>One answer to the question the current step is asking.</summary>
public sealed record GuideChoice(string Label, string Hint, RelayCommand Command);

public enum GuideStep
{
    Attach,
    Subject,
    Visible,
    ExactFirst,
    ExactNarrow,
    SnapshotFirst,
    Direction,
    Nothing,
    Few,
    Stable,
    Done
}

/// <summary>
/// Walks a first-time user through one whole search, in their words rather than the tool's.
///
/// The research at the start of this project was blunt about it: what stops people using a memory
/// scanner is not missing capability, it is that nothing tells them what to do next. Everything
/// else here serves someone who already knows the loop — search, change it in the game, search
/// again. This serves everyone else.
///
/// It drives the real scan commands rather than reimplementing them, so it cannot drift away from
/// what the app actually does, and every step says what to do in the *game* rather than in the app.
/// </summary>
public sealed class GuideViewModel : ObservableObject
{
    /// <summary>Few enough candidates to keep them all rather than keep narrowing.</summary>
    private const int FewEnough = 12;

    private readonly MainViewModel _main;
    private GuideStep _step;
    private string _subject = string.Empty;
    private bool _isActive;
    private bool _busy;

    public GuideViewModel(MainViewModel main)
    {
        _main = main;
        _main.PropertyChanged += OnMainChanged;

        PrimaryCommand = new RelayCommand(async () => await PrimaryAsync(), () => !_busy && !_main.IsScanning);
        DismissCommand = new RelayCommand(Dismiss);

        _step = GuideStep.Attach;
    }

    /// <summary>Raised when the user asks for a route to the value they have just saved.</summary>
    public event Action? StableAddressRequested;

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            Set(ref _isActive, value);
        }
    }

    public GuideStep Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value)) return;
            RaiseStep();
        }
    }

    /// <summary>What the user said they are trying to change. It names the cheat at the end.</summary>
    public string Subject
    {
        get => _subject;
        set => Set(ref _subject, value);
    }

    public RelayCommand PrimaryCommand { get; }

    public RelayCommand DismissCommand { get; }

    public ObservableCollection<GuideChoice> Choices { get; } = [];

    public void Show()
    {
        Step = _main.IsAttached ? GuideStep.Subject : GuideStep.Attach;
        IsActive = true;
        RaiseStep();
    }

    public void Dismiss()
    {
        IsActive = false;
        _main.Settings.GuideDismissed = true;
        _main.Settings.Save();
    }

    // ------------------------------------------------------------------ what each step says

    public string Title => Step switch
    {
        GuideStep.Attach => "Pick the game",
        GuideStep.Subject => "What are you trying to change?",
        GuideStep.Visible => "Can you see a number for it?",
        GuideStep.ExactFirst => "Type the number you can see",
        GuideStep.ExactNarrow => "Now change it in the game",
        GuideStep.SnapshotFirst => "Take a copy of the game's memory",
        GuideStep.Direction => "Change it, then say which way it went",
        GuideStep.Nothing => "Nothing matched",
        GuideStep.Few => "That is few enough",
        GuideStep.Stable => "Make it survive a restart",
        _ => "That is the whole loop"
    };

    public string Body => Step switch
    {
        GuideStep.Attach =>
            "Start the game first, then choose it here. Everything after this happens while it is running.",

        GuideStep.Subject =>
            "Name the thing you want to change — health, money, ammo. This only labels the cheat at the "
            + "end; the search itself does not care what it is called.",

        GuideStep.Visible =>
            "If the game shows the amount as digits, it can be searched for directly. If it is a bar, or "
            + "a number that never quite matches what is stored, there is another way in — and it is the "
            + "one almost nobody finds on their own.",

        GuideStep.ExactFirst =>
            "Exactly as the game shows it. Decimals are fine. The first search reads the whole game, so "
            + "give it a moment.",

        GuideStep.ExactNarrow =>
            "Take damage, spend the money, fire a shot — whatever moves the number. Then come back and "
            + "type what it says now. Repeat until only a few addresses are left.",

        GuideStep.SnapshotFirst =>
            "There is no number to search for, so the search compares the game against itself. Capture it "
            + "now, while the value is steady, and then go and change it.",

        GuideStep.Direction =>
            "Go and make it change in the game — lose some health, spend some money. Then say what "
            + "happened to it. Each answer throws away everything that did not do the same.",

        GuideStep.Nothing =>
            "Two things usually cause this. The number on screen may not be stored the way it is shown, "
            + "or the value moved before the search finished. Starting again with the no-number route "
            + "finds values that a direct search cannot.",

        GuideStep.Few =>
            "These all behaved the way you described. There is no need to work out which one is the real "
            + "one — keep them together under one name and freezing them all works just as well.",

        GuideStep.Stable =>
            "The addresses you just saved are where the value lives today. Next time the game starts it "
            + "will be somewhere else, and a saved address alone stops working. Tracing a route to it "
            + "from a fixed point in the program is what makes it keep working.",

        _ =>
            "Search for what you can see, change it in the game, search again, then save what is left. "
            + "That is the whole thing. This guide will not open by itself again — the Guide button in "
            + "the bar brings it back."
    };

    /// <summary>The label on the button that moves the step on, or empty when only choices are offered.</summary>
    public string PrimaryLabel => Step switch
    {
        GuideStep.Attach => "Choose a process",
        GuideStep.Subject => "Continue",
        GuideStep.ExactFirst => "Search",
        GuideStep.ExactNarrow => "Search again",
        GuideStep.SnapshotFirst => "Capture now",
        GuideStep.Few => "Save them as one cheat",
        GuideStep.Done => "Finish",
        _ => string.Empty
    };

    public bool HasPrimary => PrimaryLabel.Length > 0;

    public bool ShowSubjectBox => Step == GuideStep.Subject;

    public bool ShowValueBox => Step is GuideStep.ExactFirst or GuideStep.ExactNarrow;

    /// <summary>How the search is going, in the only terms that matter at this point: how many are left.</summary>
    public string ProgressText
    {
        get
        {
            if (Step is GuideStep.Attach or GuideStep.Subject or GuideStep.Visible) return string.Empty;
            if (_main.Results is not { } results) return string.Empty;
            return results.TotalCount == 1
                ? "1 address still matches"
                : $"{results.TotalCount:N0} addresses still match";
        }
    }

    public string SubjectLabel => string.IsNullOrWhiteSpace(Subject) ? "this" : Subject.Trim();

    // ------------------------------------------------------------------ moving between steps

    private async Task PrimaryAsync()
    {
        switch (Step)
        {
            case GuideStep.Attach:
                _main.AttachCommand.Execute(null);
                break;

            case GuideStep.Subject:
                Step = GuideStep.Visible;
                break;

            case GuideStep.ExactFirst:
                await SearchAsync(CompareKind.EqualTo);
                break;

            case GuideStep.ExactNarrow:
                await SearchAsync(CompareKind.EqualTo);
                break;

            case GuideStep.SnapshotFirst:
                _busy = true;
                PrimaryCommand.RaiseCanExecuteChanged();
                await _main.CaptureSnapshotAsync();
                _busy = false;
                PrimaryCommand.RaiseCanExecuteChanged();
                Step = GuideStep.Direction;
                break;

            case GuideStep.Few:
                SaveResults();
                break;

            case GuideStep.Done:
                Dismiss();
                break;
        }
    }

    /// <summary>
    /// Runs the app's own scan rather than a copy of it, so the guide cannot quietly do something
    /// different from what the buttons beside it do.
    /// </summary>
    private async Task SearchAsync(CompareKind kind)
    {
        if (!_main.IsAttached) { Step = GuideStep.Attach; return; }

        _main.SelectCompare(kind);

        _busy = true;
        PrimaryCommand.RaiseCanExecuteChanged();
        try
        {
            await _main.RunScanFromGuideAsync();
        }
        finally
        {
            _busy = false;
            PrimaryCommand.RaiseCanExecuteChanged();
        }

        JudgeResults();
    }

    private void JudgeResults()
    {
        int count = _main.Results?.TotalCount ?? 0;

        if (count == 0)
        {
            Step = GuideStep.Nothing;
            return;
        }

        if (count <= FewEnough)
        {
            Step = GuideStep.Few;
            return;
        }

        // Still too many: stay on the narrowing step, whichever route we came in on.
        Step = Step == GuideStep.Direction ? GuideStep.Direction : GuideStep.ExactNarrow;
        RaiseStep();
    }

    private void SaveResults()
    {
        var rows = _main.TakeResults(FewEnough);
        if (rows.Count == 0) return;

        _main.AddResults(rows, SubjectLabel);
        Step = GuideStep.Stable;
    }

    private void RebuildChoices()
    {
        Choices.Clear();

        switch (Step)
        {
            case GuideStep.Visible:
                Choices.Add(new GuideChoice("Yes, I can read the number",
                    "Search for it directly",
                    new RelayCommand(() => Step = GuideStep.ExactFirst)));
                Choices.Add(new GuideChoice("No — it is a bar, or the number never matches",
                    "Compare the game against itself instead",
                    new RelayCommand(() => Step = GuideStep.SnapshotFirst)));
                break;

            case GuideStep.Direction:
                Choices.Add(new GuideChoice("It went up",
                    "Keep only what increased",
                    new RelayCommand(async () => await SearchAsync(CompareKind.Increased))));
                Choices.Add(new GuideChoice("It went down",
                    "Keep only what decreased",
                    new RelayCommand(async () => await SearchAsync(CompareKind.Decreased))));
                Choices.Add(new GuideChoice("It changed, but I cannot say which way",
                    "Keep everything that moved",
                    new RelayCommand(async () => await SearchAsync(CompareKind.Changed))));
                Choices.Add(new GuideChoice("It has not changed at all",
                    "Keep only what stayed the same",
                    new RelayCommand(async () => await SearchAsync(CompareKind.Unchanged))));
                break;

            case GuideStep.Nothing:
                Choices.Add(new GuideChoice("Try the no-number route",
                    "Compare the game against itself",
                    new RelayCommand(() => { _main.StartOver(); Step = GuideStep.SnapshotFirst; })));
                Choices.Add(new GuideChoice("Start again with a number",
                    "Type what the game shows now",
                    new RelayCommand(() => { _main.StartOver(); Step = GuideStep.ExactFirst; })));
                break;

            case GuideStep.Few:
                Choices.Add(new GuideChoice("Keep narrowing instead",
                    "Change it in the game once more",
                    new RelayCommand(() => Step = _main.HasSnapshot ? GuideStep.Direction : GuideStep.ExactNarrow)));
                break;

            case GuideStep.Stable:
                Choices.Add(new GuideChoice("Trace a route now",
                    "Takes a minute, and the cheat still works tomorrow",
                    new RelayCommand(() =>
                    {
                        StableAddressRequested?.Invoke();
                        Step = GuideStep.Done;
                    })));
                Choices.Add(new GuideChoice("Not now",
                    "The cheat works until the game closes",
                    new RelayCommand(() => Step = GuideStep.Done)));
                break;
        }
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsAttached):
                if (Step == GuideStep.Attach && _main.IsAttached) Step = GuideStep.Subject;
                break;

            case nameof(MainViewModel.IsScanning):
                PrimaryCommand.RaiseCanExecuteChanged();
                break;

            case nameof(MainViewModel.Results):
                Raise(nameof(ProgressText));
                break;
        }
    }

    private void RaiseStep()
    {
        RebuildChoices();
        Raise(nameof(Title));
        Raise(nameof(Body));
        Raise(nameof(PrimaryLabel));
        Raise(nameof(HasPrimary));
        Raise(nameof(ShowSubjectBox));
        Raise(nameof(ShowValueBox));
        Raise(nameof(ProgressText));
        Raise(nameof(SubjectLabel));
    }
}
