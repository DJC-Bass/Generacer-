using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Persistent player inventory. A DontDestroyOnLoad singleton so purchased items
/// (and the credit balance) follow the player from the HUB world to the
/// TrackScene and back. Auto-bootstrapped at startup via a
/// RuntimeInitializeOnLoadMethod, so it needs ZERO scene setup — there is no
/// component to place. The store writes to it; the inventory view reads from it.
/// </summary>
public class PlayerInventory : MonoBehaviour
{
    /// <summary>An item + quantity granted at the start of a play session.</summary>
    [System.Serializable]
    public struct StartingStack
    {
        public string itemName;
        public int amount;
    }

    public static PlayerInventory Instance { get; private set; }

    // The inventory is created automatically at startup (no scene component), so
    // edit these starting-item defaults here in code.
    [SerializeField]
    private StartingStack[] startingItems =
    {
        new StartingStack { itemName = "Turbo Canister", amount = 4 },
    };
    private bool startingItemsSeeded;

    // Credits the player starts the session with — and is reset to when they fail a
    // run (kill floor / timeout). Code-configured like startingItems because the
    // inventory is bootstrapped without a scene component to edit in the Inspector.
    [SerializeField]
    private int startingCredits = 200;

    // item name -> quantity owned
    private readonly Dictionary<string, int> counts = new Dictionary<string, int>();
    // first-acquired order, so the inventory view lists items in a stable order
    private readonly List<string> order = new List<string>();

    /// <summary>Current spendable credits. Only meaningful when a store has
    /// Enforce Credits enabled; otherwise purchases never touch it.</summary>
    public int Credits { get; private set; }
    private bool creditsSeeded;

    /// <summary>Fires whenever counts or credits change, so open UIs can refresh.</summary>
    public event Action OnChanged;

    /// <summary>Name of the SD item the player currently has equipped ("" if none). Persists
    /// across scenes with this singleton; the (future) SD ability reads it.</summary>
    public string EquippedSD { get; private set; } = "";

    /// <summary>Sets the equipped SD. The SD HUD calls this as the player switches with the
    /// D-pad; read EquippedSD elsewhere to activate the SD's ability.</summary>
    public void SetEquippedSD(string itemName) => EquippedSD = itemName ?? "";

    /// <summary>
    /// Creates the persistent "PlayerSystems" object once at startup with the
    /// inventory data layer, the inventory-view UI, the credits HUD, and the
    /// Turbo/Jet HUD. Runs before the first scene loads, so all are available
    /// everywhere.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("PlayerSystems");
        go.AddComponent<PlayerInventory>();   // sets Instance in Awake (added first)
        go.AddComponent<InventoryView>();
        go.AddComponent<CreditsHUD>();
        go.AddComponent<TurboJetHUD>();
        go.AddComponent<LraAbortController>();   // L+R+A hold-to-abort + its progress bar
        go.AddComponent<SDCardHUD>();            // equipped-SD readout + D-pad switching
        go.AddComponent<SDAbilityController>();  // D-pad up: toggle the equipped SD's ability
        go.AddComponent<PlayerCarSwapper>();     // swaps in the Car-Selection choice each gameplay scene
        DontDestroyOnLoad(go);
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        SeedStartingItems();
        SeedCreditsOnce(startingCredits);   // start the session at the default credits
    }

    /// <summary>Grants the configured starting items once per play session.</summary>
    void SeedStartingItems()
    {
        if (startingItemsSeeded) return;
        startingItemsSeeded = true;
        if (startingItems == null) return;

        foreach (var stack in startingItems)
            if (!string.IsNullOrEmpty(stack.itemName) && stack.amount > 0)
                Add(stack.itemName, stack.amount);
    }

    public int GetCount(string itemName)
        => counts.TryGetValue(itemName, out int c) ? c : 0;

    /// <summary>Item names in first-acquired order (for the inventory view).</summary>
    public IReadOnlyList<string> Order => order;

    /// <summary>Seeds the credit balance once per play session. Repeat calls
    /// (e.g. each time the hub scene reloads) are ignored so credits aren't
    /// reset on every return to the hub.</summary>
    public void SeedCreditsOnce(int amount)
    {
        if (creditsSeeded) return;
        creditsSeeded = true;
        Credits = amount;
        OnChanged?.Invoke();
    }

    public void AddCredits(int amount)
    {
        Credits += amount;
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Wipes all owned items and credits and restores the starting defaults
    /// (startingItems + startingCredits). This is the failure penalty applied when
    /// the player returns to the hub WITHOUT completing the track — via the kill
    /// floor or by running out of time. Completing the track (the End Portal) never
    /// calls this, so a successful run keeps everything the player earned/bought.
    /// </summary>
    public void ResetToStarting()
    {
        counts.Clear();
        order.Clear();

        // Re-grant the starting items. Add() repopulates counts + order per stack.
        if (startingItems != null)
            foreach (var stack in startingItems)
                if (!string.IsNullOrEmpty(stack.itemName) && stack.amount > 0)
                    Add(stack.itemName, stack.amount);

        Credits = startingCredits;
        EquippedSD = "";                 // SDs are wiped on a failure, so nothing stays equipped
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Buys <paramref name="amount"/> of <paramref name="itemName"/>. Returns true
    /// on success. Enforces the cap (<paramref name="maxOwned"/> of 0 or less =
    /// unlimited; the whole amount must fit under it) and, when
    /// <paramref name="charge"/> is true, the price against the credit balance.
    /// One store row can grant several of an item (e.g. a "Jet Fuel Pack" that
    /// grants 5 "Jet Fuel"), hence the amount.
    /// </summary>
    public bool TryPurchase(string itemName, int amount, int maxOwned, int price, bool charge)
    {
        if (amount < 1) amount = 1;
        int current = GetCount(itemName);
        if (maxOwned > 0 && current + amount > maxOwned) return false;  // wouldn't fit under cap
        if (charge && Credits < price) return false;                    // can't afford

        if (charge) Credits -= price;
        Add(itemName, amount);   // handles first-acquire order tracking + OnChanged
        return true;
    }

    /// <summary>
    /// Adds <paramref name="amount"/> of an item with no cap or cost check
    /// (e.g. a crafted product). Use TryPurchase for purchases that must respect caps.
    /// </summary>
    public void Add(string itemName, int amount)
    {
        if (amount <= 0) return;
        int current = GetCount(itemName);
        if (current == 0 && !counts.ContainsKey(itemName)) order.Add(itemName);
        counts[itemName] = current + amount;
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Removes <paramref name="amount"/> of an item. Returns false (and changes
    /// nothing) if the player doesn't have that many.
    /// </summary>
    public bool Consume(string itemName, int amount = 1)
    {
        int current = GetCount(itemName);
        if (amount <= 0 || current < amount) return false;
        counts[itemName] = current - amount;
        OnChanged?.Invoke();
        return true;
    }
}
