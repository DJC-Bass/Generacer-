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
