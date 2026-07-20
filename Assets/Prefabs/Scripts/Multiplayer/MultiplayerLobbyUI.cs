using System;
using System.Collections.Generic;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// The Online Multiplayer lobby flow (Phase 1), code-built on the MainMenu canvas in the same style
/// as the rest of the menu. Added at runtime by <see cref="MainMenuController.BuildUI"/> and opened
/// by the ONLINE MULTIPLAYER button; while <see cref="IsOpen"/> the owner menu suspends its own
/// input/navigation handling and this component runs its own.
///
/// Screens: ROOT (host / join / browser) → HOST SETTINGS (players-per-team lobby rule + visibility)
/// → the LOBBY ROOM (join code, two team columns, switch-team / car / ready-up, host START GAME),
/// plus JOIN WITH CODE (keyboard entry) and a public-session BROWSER (the gamepad-friendly path).
/// All session traffic goes through <see cref="NetworkSessionManager"/>; by the time the room screen
/// is up, Relay is allocated and NGO host/client is already running (the Sessions API starts it).
/// </summary>
public class MultiplayerLobbyUI : MonoBehaviour
{
    public bool IsOpen { get; private set; }

    private enum Screen { Root, Host, Join, Browser, Room }
    private Screen screen = Screen.Root;

    // Wiring from MainMenuController.Configure
    private MainMenuController owner;
    private Transform canvasRoot;
    private CarSelectionController.CarEntry[] cars;
    private Action onExit;

    private bool built;
    private bool busy;   // an async session op is in flight — ignore input until it lands

    // Panels
    private GameObject rootPanel, hostPanel, joinPanel, browserPanel, roomPanel;
    private TextMeshProUGUI statusText;
    private GameObject currentFirst;          // this screen's navigation-rescue target
    private GameObject lastSelectedForSfx;

    // Host screen
    private OptionSelector teamSizeCycler, visibilityCycler;

    // Join screen
    private TMP_InputField codeInput;

    // Browser screen
    private Transform browserListParent;
    private Button browserRefreshButton, browserBackButton;
    private readonly List<Button> browserRows = new List<Button>();

    // Room screen
    private TextMeshProUGUI roomTitle, roomCode, teamOneList, teamTwoList;
    private Button switchTeamButton, readyButton, startButton, leaveButton;
    private OptionSelector carCycler;
    private string[] carNames;

    private NetworkSessionManager Manager => NetworkSessionManager.Instance;

    /// <summary>Called once by MainMenuController right after it builds the canvas.</summary>
    public void Configure(MainMenuController owner, Transform canvasRoot,
                          CarSelectionController.CarEntry[] cars, Action onExit)
    {
        this.owner = owner;
        this.canvasRoot = canvasRoot;
        this.cars = cars;
        this.onExit = onExit;
    }

    public void Open()
    {
        var manager = NetworkSessionManager.EnsureExists();
        manager.SessionUpdated -= OnSessionUpdated;
        manager.SessionUpdated += OnSessionUpdated;
        manager.SessionEnded -= OnSessionEnded;
        manager.SessionEnded += OnSessionEnded;

        if (!built) BuildAll();
        IsOpen = true;
        SetStatus("");
        if (manager.InSession) ShowRoom();
        else ShowRoot();
    }

    void OnDestroy()
    {
        var manager = NetworkSessionManager.Instance;
        if (manager != null)
        {
            manager.SessionUpdated -= OnSessionUpdated;
            manager.SessionEnded -= OnSessionEnded;
        }
    }

    void Update()
    {
        if (!IsOpen || busy) return;
        if (!BackPressed()) return;

        AudioManager.PlayMenuBack();
        switch (screen)
        {
            case Screen.Root: Exit(); break;
            case Screen.Host:
            case Screen.Join:
            case Screen.Browser: ShowRoot(); break;
            case Screen.Room: OnLeavePressed(); break;   // B in the room = leave the lobby
        }
    }

    void LateUpdate()
    {
        if (!IsOpen) return;
        MenuNavigation.EnsureSelectionOnNavigate(currentFirst);
        MenuNavigation.PlayMoveSfxOnSelectionChange(ref lastSelectedForSfx);
    }

    static bool BackPressed()
    {
#if ENABLE_INPUT_SYSTEM
        var gp = Gamepad.current;
        if (gp != null && gp.buttonEast.wasPressedThisFrame) return true;
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) return true;
#endif
        return false;
    }

    // -------------------------------------------------------
    //  Screen switching
    // -------------------------------------------------------

    void SetPanels(bool root, bool host, bool join, bool browser, bool room)
    {
        rootPanel.SetActive(root);
        hostPanel.SetActive(host);
        joinPanel.SetActive(join);
        browserPanel.SetActive(browser);
        roomPanel.SetActive(room);
    }

    void ShowRoot()
    {
        screen = Screen.Root;
        SetPanels(true, false, false, false, false);
        Focus(rootPanel.GetComponentInChildren<Button>(true)?.gameObject);
    }

    void ShowHost()
    {
        screen = Screen.Host;
        SetPanels(false, true, false, false, false);
        Focus(teamSizeCycler != null ? teamSizeCycler.gameObject : null);
    }

    void ShowJoin()
    {
        screen = Screen.Join;
        SetPanels(false, false, true, false, false);
        SetStatus("TYPE THE CODE WITH A KEYBOARD, THEN JOIN");
        Focus(codeInput != null ? codeInput.gameObject : null);
    }

    void ShowBrowser()
    {
        screen = Screen.Browser;
        SetPanels(false, false, false, true, false);
        Focus(browserRefreshButton != null ? browserRefreshButton.gameObject : null);
        RefreshBrowser();
    }

    void ShowRoom()
    {
        screen = Screen.Room;
        SetPanels(false, false, false, false, true);
        WireRoomNavigation();
        RefreshRoom();
        Focus(readyButton != null ? readyButton.gameObject : null);
    }

    void Exit()
    {
        IsOpen = false;
        SetPanels(false, false, false, false, false);
        onExit?.Invoke();
    }

    void Focus(GameObject go)
    {
        currentFirst = go;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(go);
        lastSelectedForSfx = go;
    }

    void SetStatus(string message)
    {
        if (statusText != null) statusText.text = message ?? "";
    }

    // -------------------------------------------------------
    //  Session events
    // -------------------------------------------------------

    void OnSessionUpdated()
    {
        if (IsOpen && screen == Screen.Room && Manager != null && Manager.InSession)
            RefreshRoom();
    }

    void OnSessionEnded(string reason)
    {
        if (!IsOpen) return;
        AudioManager.PlayStoreDenied();
        SetStatus(reason);
        ShowRoot();
    }

    // -------------------------------------------------------
    //  Button handlers (async ops set `busy` and report via the status line)
    // -------------------------------------------------------

    async void OnCreateLobbyPressed()
    {
        if (busy) return;
        busy = true;
        SetStatus("SIGNING IN + CREATING LOBBY...");
        try
        {
            int teamSize = NetworkSessionManager.MinTeamSize + (teamSizeCycler != null ? teamSizeCycler.Index : 2);
            bool isPrivate = visibilityCycler != null && visibilityCycler.Index == 1;
            await NetworkSessionManager.EnsureExists().HostSessionAsync(null, teamSize, isPrivate);
            SetStatus("");
            ShowRoom();
        }
        catch (Exception e) { FailStatus(e); }
        finally { busy = false; }
    }

    async void OnJoinByCodePressed()
    {
        if (busy) return;
        string code = codeInput != null ? codeInput.text.Trim() : "";
        if (string.IsNullOrEmpty(code)) { SetStatus("ENTER A JOIN CODE FIRST"); return; }
        busy = true;
        SetStatus("JOINING...");
        try
        {
            await NetworkSessionManager.EnsureExists().JoinByCodeAsync(code);
            SetStatus("");
            ShowRoom();
        }
        catch (Exception e) { FailStatus(e); }
        finally { busy = false; }
    }

    async void OnJoinByIdPressed(string sessionId, string sessionName)
    {
        if (busy) return;
        busy = true;
        SetStatus("JOINING " + sessionName + "...");
        try
        {
            await NetworkSessionManager.EnsureExists().JoinByIdAsync(sessionId);
            SetStatus("");
            ShowRoom();
        }
        catch (Exception e) { FailStatus(e); }
        finally { busy = false; }
    }

    async void RefreshBrowser()
    {
        if (busy) return;
        busy = true;
        SetStatus("SEARCHING FOR LOBBIES...");
        try
        {
            var sessions = await NetworkSessionManager.EnsureExists().QueryPublicSessionsAsync();
            RebuildBrowserRows(sessions);
            SetStatus(sessions.Count == 0 ? "NO PUBLIC LOBBIES FOUND" : "");
        }
        catch (Exception e) { FailStatus(e); }
        finally { busy = false; }
    }

    async void OnSwitchTeamPressed()
    {
        if (busy || Manager == null) return;
        busy = true;
        try
        {
            bool ok = await Manager.TrySwitchTeamAsync();
            if (!ok) { AudioManager.PlayStoreDenied(); SetStatus("THAT TEAM IS FULL"); }
            else SetStatus("");
        }
        catch (Exception e) { FailStatus(e); }
        finally { busy = false; }
    }

    async void OnReadyPressed()
    {
        if (busy || Manager == null || Manager.Session == null) return;
        busy = true;
        try
        {
            bool ready = NetworkSessionManager.IsReady(Manager.Session.CurrentPlayer);
            await Manager.SetLocalPlayerPropertyAsync(NetworkSessionManager.PlayerPropReady, ready ? "0" : "1");
        }
        catch (Exception e) { FailStatus(e); }
        finally { busy = false; }
    }

    async void OnStartGamePressed()
    {
        if (busy || Manager == null || !Manager.IsSessionHost) return;
        if (!Manager.ReadyToStart(out string reason)) { AudioManager.PlayStoreDenied(); SetStatus(reason); return; }
        busy = true;
        SetStatus("STARTING...");
        try
        {
            await Manager.StartGameAsync();
            // The lobby locks and the "started" flag replicates to every member;
            // NetworkSessionManager sees it and launches the shared world on every machine.
            SetStatus("STARTING — LOADING THE WORLD...");
        }
        catch (Exception e) { FailStatus(e); }
        finally { busy = false; }
    }

    async void OnLeavePressed()
    {
        if (busy || Manager == null) return;
        busy = true;
        SetStatus("LEAVING...");
        try
        {
            await Manager.LeaveSessionAsync();
            SetStatus("");
        }
        catch (Exception e) { FailStatus(e); }
        finally
        {
            busy = false;
            ShowRoot();
        }
    }

    async void OnCarChanged(int index)
    {
        if (cars == null || index < 0 || index >= cars.Length) return;
        SelectedCarStore.Set(cars[index].name, cars[index].prefab);
        if (Manager != null && Manager.InSession)
        {
            try { await Manager.SetLocalPlayerPropertyAsync(NetworkSessionManager.PlayerPropCar, cars[index].name); }
            catch (Exception e) { FailStatus(e); }
        }
    }

    void FailStatus(Exception e)
    {
        AudioManager.PlayStoreDenied();
        string msg = e.Message ?? "UNKNOWN ERROR";
        if (msg.Length > 90) msg = msg.Substring(0, 90);
        // The most common first-run failure: the project isn't linked to a UGS project id yet.
        if (e is Unity.Services.Core.ServicesInitializationException)
            msg += "  (IS THE PROJECT LINKED IN PROJECT SETTINGS > SERVICES?)";
        SetStatus(msg.ToUpperInvariant());
        Debug.LogWarning($"[MultiplayerLobby] {e}");
    }

    // -------------------------------------------------------
    //  Construction
    // -------------------------------------------------------

    SettingsUI.Theme Theme => new SettingsUI.Theme
    {
        normal = owner.buttonNormalColor,
        highlighted = owner.buttonHighlightedColor,
        selected = owner.buttonSelectedColor,
        pressed = owner.buttonPressedColor,
        text = owner.buttonTextColor,
        fade = owner.buttonColorFadeDuration,
    };

    void BuildAll()
    {
        built = true;

        carNames = (cars != null && cars.Length > 0)
            ? Array.ConvertAll(cars, c => string.IsNullOrEmpty(c.name) ? "?" : c.name)
            : new[] { "DEFAULT" };

        rootPanel = BuildRootPanel();
        hostPanel = BuildHostPanel();
        joinPanel = BuildJoinPanel();
        browserPanel = BuildBrowserPanel();
        roomPanel = BuildRoomPanel();

        // Shared status line, under whichever panel is up.
        statusText = SettingsUI.NewText(canvasRoot, "MultiplayerStatus", 26f, TextAlignmentOptions.TopLeft);
        statusText.color = new Color(0.95f, 0.75f, 0.45f, 1f);
        var srt = statusText.rectTransform;
        srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0f, 1f);
        srt.sizeDelta = new Vector2(1400f, 60f);
        srt.anchoredPosition = new Vector2(96f, -940f);

        SetPanels(false, false, false, false, false);
    }

    GameObject BuildRootPanel()
    {
        var panel = NewPanel("MultiplayerRoot");
        AddHeader(panel.transform, "ONLINE MULTIPLAYER");

        var column = NewButtonColumn(panel.transform, new Vector2(0f, -90f));
        var host = MakeButton("HOST LOBBY", column, ShowHost);
        var join = MakeButton("JOIN WITH CODE", column, ShowJoin);
        var browse = MakeButton("LOBBY BROWSER", column, ShowBrowser);
        var back = MakeButton("BACK", column, () => { AudioManager.PlayMenuBack(); Exit(); });
        MenuNavigation.WireVerticalWrap(new[] { host, join, browse, back });
        return panel;
    }

    GameObject BuildHostPanel()
    {
        var panel = NewPanel("MultiplayerHost");
        AddHeader(panel.transform, "HOST LOBBY");

        // The lobby RULES — team size is a session property, never a hard-coded 3.
        var sizeLabels = new List<string>();
        for (int i = NetworkSessionManager.MinTeamSize; i <= NetworkSessionManager.MaxTeamSize; i++)
            sizeLabels.Add(i.ToString());
        teamSizeCycler = AddOptionRow(panel.transform, "PLAYERS PER TEAM", -110f, sizeLabels,
                                      NetworkSessionManager.DefaultTeamSize - NetworkSessionManager.MinTeamSize, _ => { });

        visibilityCycler = AddOptionRow(panel.transform, "VISIBILITY", -180f,
                                        new List<string> { "PUBLIC", "PRIVATE" }, 0, _ => { });

        var column = NewButtonColumn(panel.transform, new Vector2(0f, -270f));
        var create = MakeButton("CREATE LOBBY", column, OnCreateLobbyPressed);
        var back = MakeButton("BACK", column, () => { AudioManager.PlayMenuBack(); ShowRoot(); });

        SettingsUI.WireVerticalWrap(new List<Selectable> { teamSizeCycler, visibilityCycler, create, back });
        return panel;
    }

    GameObject BuildJoinPanel()
    {
        var panel = NewPanel("MultiplayerJoin");
        AddHeader(panel.transform, "JOIN WITH CODE");

        codeInput = MakeCodeInput(panel.transform, new Vector2(0f, -110f));

        var column = NewButtonColumn(panel.transform, new Vector2(0f, -210f));
        var join = MakeButton("JOIN", column, OnJoinByCodePressed);
        var back = MakeButton("BACK", column, () => { AudioManager.PlayMenuBack(); ShowRoot(); });

        SettingsUI.WireVerticalWrap(new List<Selectable> { codeInput, join, back });
        return panel;
    }

    GameObject BuildBrowserPanel()
    {
        var panel = NewPanel("MultiplayerBrowser");
        AddHeader(panel.transform, "LOBBY BROWSER");

        var column = NewButtonColumn(panel.transform, new Vector2(0f, -90f));
        browserListParent = column;
        browserRefreshButton = MakeButton("REFRESH", column, RefreshBrowser);
        browserBackButton = MakeButton("BACK", column, () => { AudioManager.PlayMenuBack(); ShowRoot(); });
        WireBrowserNavigation();
        return panel;
    }

    GameObject BuildRoomPanel()
    {
        var panel = NewPanel("MultiplayerRoom");
        ((RectTransform)panel.transform).sizeDelta = new Vector2(1750f, 800f);

        roomTitle = AddHeader(panel.transform, "LOBBY");

        roomCode = SettingsUI.NewText(panel.transform, "JoinCode", 34f, TextAlignmentOptions.TopLeft);
        roomCode.color = owner.buttonHighlightedColor;
        var crt = roomCode.rectTransform;
        crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0f, 1f);
        crt.sizeDelta = new Vector2(700f, 44f);
        crt.anchoredPosition = new Vector2(0f, -66f);

        // Left: the local player's controls.
        var column = NewButtonColumn(panel.transform, new Vector2(0f, -130f));
        switchTeamButton = MakeButton("SWITCH TEAM", column, OnSwitchTeamPressed);

        carCycler = SettingsUI.OptionCycler(column, Theme, 30f, carNames, InitialCarIndex(), OnCarChanged);
        var carLE = carCycler.gameObject.AddComponent<LayoutElement>();
        carLE.preferredWidth = owner.buttonSize.x; carLE.minWidth = owner.buttonSize.x;
        carLE.preferredHeight = owner.buttonSize.y; carLE.minHeight = owner.buttonSize.y;

        readyButton = MakeButton("READY: NO", column, OnReadyPressed);
        startButton = MakeButton("START GAME", column, OnStartGamePressed);
        leaveButton = MakeButton("LEAVE LOBBY", column, OnLeavePressed);

        // Right: the two team rosters.
        teamOneList = MakeTeamColumn(panel.transform, "TEAM 1", 700f);
        teamTwoList = MakeTeamColumn(panel.transform, "TEAM 2", 1220f);
        return panel;
    }

    TextMeshProUGUI MakeTeamColumn(Transform parent, string header, float x)
    {
        var head = SettingsUI.NewText(parent, header + "Header", 36f, TextAlignmentOptions.TopLeft);
        head.text = header;
        head.fontStyle |= FontStyles.Bold;
        head.color = owner.buttonTextColor;
        var hrt = head.rectTransform;
        hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0f, 1f);
        hrt.sizeDelta = new Vector2(480f, 46f);
        hrt.anchoredPosition = new Vector2(x, -110f);

        var list = SettingsUI.NewText(parent, header + "List", 27f, TextAlignmentOptions.TopLeft);
        list.color = owner.buttonTextColor;
        var lrt = list.rectTransform;
        lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(0f, 1f);
        lrt.sizeDelta = new Vector2(480f, 420f);
        lrt.anchoredPosition = new Vector2(x, -166f);
        return list;
    }

    int InitialCarIndex()
    {
        var store = SelectedCarStore.Instance;
        if (store != null && cars != null)
            for (int i = 0; i < cars.Length; i++)
                if (cars[i].name == store.SelectedCarName) return i;
        return 0;
    }

    void WireRoomNavigation()
    {
        bool host = Manager != null && Manager.IsSessionHost;
        if (startButton != null) startButton.gameObject.SetActive(host);

        var items = new List<Selectable> { switchTeamButton, carCycler, readyButton };
        if (host) items.Add(startButton);
        items.Add(leaveButton);
        SettingsUI.WireVerticalWrap(items);
    }

    void WireBrowserNavigation()
    {
        var items = new List<Selectable> { browserRefreshButton };
        foreach (var row in browserRows) if (row != null) items.Add(row);
        items.Add(browserBackButton);
        SettingsUI.WireVerticalWrap(items);
    }

    void RebuildBrowserRows(IList<ISessionInfo> sessions)
    {
        foreach (var row in browserRows) if (row != null) Destroy(row.gameObject);
        browserRows.Clear();

        if (sessions != null)
        {
            foreach (var info in sessions)
            {
                string teamSize = "?";
                if (info.Properties != null &&
                    info.Properties.TryGetValue(NetworkSessionManager.SessionPropTeamSize, out var p))
                    teamSize = p.Value;

                string name = string.IsNullOrEmpty(info.Name) ? "LOBBY" : info.Name;
                if (name.Length > 20) name = name.Substring(0, 20);
                int playerCount = info.MaxPlayers - info.AvailableSlots;
                string label = $"{name}   {playerCount}/{info.MaxPlayers}   ({teamSize} PER TEAM)";

                string id = info.Id;
                var row = MakeButton(label, browserListParent, () => OnJoinByIdPressed(id, name));
                row.transform.SetSiblingIndex(browserListParent.childCount - 2);   // above BACK
                var text = row.GetComponentInChildren<TextMeshProUGUI>();
                if (text != null) text.fontSize = 28f;
                browserRows.Add(row);
            }
        }
        WireBrowserNavigation();
        Focus(browserRefreshButton != null ? browserRefreshButton.gameObject : null);
    }

    /// <summary>Redraws everything session-derived on the room screen. Called on every session
    /// change; the buttons themselves are static so the current selection is never destroyed.</summary>
    void RefreshRoom()
    {
        var manager = Manager;
        var session = manager != null ? manager.Session : null;
        if (session == null) return;

        roomTitle.text = string.IsNullOrEmpty(session.Name) ? "LOBBY" : session.Name;
        roomCode.text = "JOIN CODE: " + (string.IsNullOrEmpty(session.Code) ? "—" : session.Code);

        var one = new System.Text.StringBuilder();
        var two = new System.Text.StringBuilder();
        var unassigned = new System.Text.StringBuilder();
        foreach (var p in session.Players)
        {
            var line = PlayerLine(session, p);
            switch (NetworkSessionManager.TeamOf(p))
            {
                case 1: one.AppendLine(line); break;
                case 2: two.AppendLine(line); break;
                default: unassigned.AppendLine(line); break;
            }
        }
        int cap = manager.TeamSize;
        teamOneList.text = one.ToString() + FreeSlotLines(cap - manager.CountTeam(1));
        teamTwoList.text = two.ToString() + FreeSlotLines(cap - manager.CountTeam(2));
        if (unassigned.Length > 0) teamOneList.text += "\nUNASSIGNED:\n" + unassigned;

        bool ready = NetworkSessionManager.IsReady(session.CurrentPlayer);
        var readyLabel = readyButton.GetComponentInChildren<TextMeshProUGUI>();
        if (readyLabel != null) readyLabel.text = ready ? "READY: YES" : "READY: NO";

        if (manager.GameStarted)
        {
            if (startButton != null) startButton.interactable = false;
            SetStatus("STARTING — LOADING THE WORLD...");
        }
        else if (manager.IsSessionHost && startButton != null)
        {
            startButton.interactable = manager.ReadyToStart(out _);
        }
    }

    string PlayerLine(ISession session, IReadOnlyPlayer p)
    {
        string name = NetworkSessionManager.PropertyOf(p, NetworkSessionManager.PlayerPropName);
        if (string.IsNullOrEmpty(name)) name = "PLAYER";
        string car = NetworkSessionManager.PropertyOf(p, NetworkSessionManager.PlayerPropCar);
        string line = name;
        if (p.Id == session.Host) line += " (HOST)";
        if (!string.IsNullOrEmpty(car)) line += "  [" + car + "]";
        line += NetworkSessionManager.IsReady(p) ? "  ✓ READY" : "  ...";
        if (session.CurrentPlayer != null && p.Id == session.CurrentPlayer.Id) line = "▸ " + line;
        return line;
    }

    static string FreeSlotLines(int free)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < free; i++) sb.AppendLine("<alpha=#55>- OPEN SLOT -<alpha=#FF>");
        return sb.ToString();
    }

    // -------------------------------------------------------
    //  Widget helpers (matching MainMenuController's look)
    // -------------------------------------------------------

    GameObject NewPanel(string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(canvasRoot, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);   // upper-left, like the main column
        rt.sizeDelta = new Vector2(900f, 700f);
        rt.anchoredPosition = new Vector2(96f, -250f);
        go.SetActive(false);
        return go;
    }

    TextMeshProUGUI AddHeader(Transform parent, string title)
    {
        var t = SettingsUI.NewText(parent, "Header", 54f, TextAlignmentOptions.TopLeft);
        t.text = title;
        t.fontStyle |= FontStyles.Bold;
        t.color = owner.buttonTextColor;
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(1200f, 64f);
        rt.anchoredPosition = new Vector2(0f, 0f);
        return t;
    }

    Transform NewButtonColumn(Transform parent, Vector2 anchoredPos)
    {
        var go = new GameObject("Buttons", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = anchoredPos;

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = owner.buttonSpacing;
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false; vlg.childForceExpandHeight = false;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return go.transform;
    }

    Button MakeButton(string label, Transform parent, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = Color.white;   // ColorBlock states provide the real colours

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = owner.buttonSize.x; le.minWidth = owner.buttonSize.x;
        le.preferredHeight = owner.buttonSize.y; le.minHeight = owner.buttonSize.y;

        var btn = go.GetComponent<Button>();
        var cb = btn.colors;
        cb.normalColor = owner.buttonNormalColor;
        cb.highlightedColor = owner.buttonHighlightedColor;
        cb.selectedColor = owner.buttonSelectedColor;
        cb.pressedColor = owner.buttonPressedColor;
        cb.disabledColor = new Color(0.30f, 0.30f, 0.30f, 0.50f);
        cb.colorMultiplier = 1f; cb.fadeDuration = owner.buttonColorFadeDuration;
        btn.colors = cb;
        btn.onClick.AddListener(onClick);
        btn.onClick.AddListener(AudioManager.PlayMenuSelect);

        var tmp = SettingsUI.NewText(go.transform, "Label", owner.buttonFontSize * 0.82f, TextAlignmentOptions.Center);
        tmp.text = label;
        tmp.color = owner.buttonTextColor;
        var trt = tmp.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        return btn;
    }

    OptionSelector AddOptionRow(Transform panel, string label, float y, IList<string> options,
                                int startIndex, Action<int> onChanged)
    {
        var lbl = SettingsUI.NewText(panel, label + "Label", 32f, TextAlignmentOptions.MidlineLeft);
        lbl.text = label;
        lbl.color = owner.buttonTextColor;
        var lrt = lbl.rectTransform;
        lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(0f, 1f);
        lrt.sizeDelta = new Vector2(340f, 50f);
        lrt.anchoredPosition = new Vector2(0f, y);

        var sel = SettingsUI.OptionCycler(panel, Theme, 30f, options, startIndex, onChanged);
        var srt = (RectTransform)sel.transform;
        srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0f, 1f);
        srt.sizeDelta = new Vector2(300f, 50f);
        srt.anchoredPosition = new Vector2(360f, y);
        return sel;
    }

    TMP_InputField MakeCodeInput(Transform parent, Vector2 anchoredPos)
    {
        var go = new GameObject("CodeInput", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(owner.buttonSize.x, owner.buttonSize.y);
        rt.anchoredPosition = anchoredPos;

        var img = go.GetComponent<Image>();
        img.color = Color.white;

        var field = go.GetComponent<TMP_InputField>();
        field.targetGraphic = img;
        var cb = field.colors;
        cb.normalColor = owner.buttonNormalColor;
        cb.highlightedColor = owner.buttonHighlightedColor;
        cb.selectedColor = owner.buttonSelectedColor;
        cb.pressedColor = owner.buttonPressedColor;
        cb.disabledColor = new Color(0.30f, 0.30f, 0.30f, 0.50f);
        cb.colorMultiplier = 1f; cb.fadeDuration = owner.buttonColorFadeDuration;
        field.colors = cb;

        var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        area.transform.SetParent(go.transform, false);
        var art = area.GetComponent<RectTransform>();
        art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one;
        art.offsetMin = new Vector2(20f, 6f); art.offsetMax = new Vector2(-20f, -6f);

        var placeholder = SettingsUI.NewText(area.transform, "Placeholder", 32f, TextAlignmentOptions.MidlineLeft);
        placeholder.text = "CODE";
        placeholder.color = new Color(1f, 1f, 1f, 0.35f);
        var prt = placeholder.rectTransform;
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;

        var text = SettingsUI.NewText(area.transform, "Text", 32f, TextAlignmentOptions.MidlineLeft);
        text.color = owner.buttonTextColor;
        var xrt = text.rectTransform;
        xrt.anchorMin = Vector2.zero; xrt.anchorMax = Vector2.one;
        xrt.offsetMin = Vector2.zero; xrt.offsetMax = Vector2.zero;

        field.textViewport = art;
        field.textComponent = text;
        field.placeholder = placeholder;
        field.characterLimit = 10;
        field.contentType = TMP_InputField.ContentType.Alphanumeric;
        return field;
    }
}
