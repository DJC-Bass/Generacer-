using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Phase 3: the single answer to "where is the player?". Replaces the scattered
/// <c>GameObject.FindWithTag("Player")</c> sites — every gameplay system that means "THE local
/// player's car" reads <see cref="LocalCar"/> (cached, with the old FindWithTag as the resolution
/// fallback, so single-player behaviour is byte-for-byte what it was). In multiplayer it
/// additionally tracks the REMOTE players' puppet cars (driven by <see cref="RemoteCarManager"/>),
/// which is what Phase 5's entity targeting will draw from, and holds the car-name → prefab catalog
/// the remote puppets are built from (fed from <c>MainMenuController.multiplayerCars</c>, since
/// scene Inspectors don't survive scene loads but prefab asset references do).
/// </summary>
public static class PlayerRegistry
{
    // ---- Local player ----

    private static GameObject localCar;

    /// <summary>The local player's car (null when no car exists, e.g. menu scenes). Resolution:
    /// whatever was explicitly registered, else the first "Player"-tagged object — remote puppet
    /// cars are deliberately NOT tagged "Player", so this can never grab someone else's car.</summary>
    public static GameObject LocalCar
    {
        get
        {
            if (localCar == null) localCar = GameObject.FindWithTag("Player");
            return localCar;
        }
    }

    /// <summary>Explicit registration — PlayerCarSwapper calls this the moment it spawns the chosen
    /// car so nothing races the tag lookup.</summary>
    public static void SetLocalCar(GameObject car) => localCar = car;

    // ---- Remote players (multiplayer only; empty in single-player) ----

    public class RemotePlayer
    {
        public ulong ClientId;
        public string Name;
        public string CarName;
        public int Team;         // 1 or 2 (0 = unknown)
        public GameObject Car;   // the local puppet driven by RemoteCarPuppet
        public bool InTrack;     // is this player currently in the TrackScene? (bit on the car-state stream)
    }

    private static readonly List<RemotePlayer> remotes = new List<RemotePlayer>();

    /// <summary>Every known remote player and their puppet car. Entity targeting (Phase 5) picks
    /// from these + <see cref="LocalCar"/>.</summary>
    public static IReadOnlyList<RemotePlayer> Remotes => remotes;

    public static RemotePlayer FindRemote(ulong clientId)
    {
        foreach (var r in remotes)
            if (r.ClientId == clientId) return r;
        return null;
    }

    internal static RemotePlayer AddOrUpdateRemote(ulong clientId, string name, string carName, int team)
    {
        var entry = FindRemote(clientId);
        if (entry == null)
        {
            entry = new RemotePlayer { ClientId = clientId };
            remotes.Add(entry);
        }
        entry.Name = name;
        entry.CarName = carName;
        entry.Team = team;
        return entry;
    }

    internal static void RemoveRemote(ulong clientId)
    {
        for (int i = remotes.Count - 1; i >= 0; i--)
        {
            if (remotes[i].ClientId != clientId) continue;
            if (remotes[i].Car != null) Object.Destroy(remotes[i].Car);
            remotes.RemoveAt(i);
        }
    }

    internal static void ClearRemotes()
    {
        foreach (var r in remotes)
            if (r.Car != null) Object.Destroy(r.Car);
        remotes.Clear();
    }

    // ---- Car catalog (name → prefab, for building remote puppets) ----

    private static CarSelectionController.CarEntry[] carCatalog;

    /// <summary>Called by MainMenuController.BuildUI with its `multiplayerCars` list. Prefab
    /// references are asset references, so the catalog outlives the menu scene.</summary>
    public static void SetCarCatalog(CarSelectionController.CarEntry[] catalog) => carCatalog = catalog;

    /// <summary>The prefab for a remote player's chosen car; falls back to the first catalog entry
    /// (better a wrong car than an invisible opponent), null only when the catalog is empty.</summary>
    public static GameObject CarPrefabFor(string carName)
    {
        if (carCatalog == null || carCatalog.Length == 0) return null;
        foreach (var entry in carCatalog)
            if (entry.name == carName && entry.prefab != null) return entry.prefab;
        return carCatalog[0].prefab;
    }
}
