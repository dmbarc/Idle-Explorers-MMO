using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The account-wide bank: 120 slots plus a coin vault, shared by every character
/// on the account.
///
/// This is what makes multiple characters worth having beyond parallel AFK timers —
/// a fisher can stock raw shrimp that a cook consumes, without either of them
/// meeting. Crafting stations resolve their inputs against the character inventory
/// first and then here, so banked material is not dead weight.
///
/// Slot mechanics come from SlotContainer, the same code the 30-slot character
/// inventory uses.
/// </summary>
public class BankManager : MonoBehaviour
{
    public const int Capacity = 120;

    /// <summary>The raw slot list, always Capacity long. Null when no account is loaded.</summary>
    public List<InventoryEntry> Items
    {
        get
        {
            var account = AccountManager.Current;
            if (account == null) return null;

            // JsonUtility writes null collections as null, and saves written before
            // the bank existed have no field at all.
            account.bank ??= new List<InventoryEntry>();

            SlotContainer.Normalize(account.bank, Capacity);
            return account.bank;
        }
    }

    public int  UsedSlots => SlotContainer.UsedSlots(Items);
    public long Coins     => AccountManager.Current?.bankCoins ?? 0;

    // ── Items ─────────────────────────────────────────────────────────────────

    public long GetQuantity(string itemId) => SlotContainer.GetQuantity(Items, itemId);

    public bool CanAddItem(string itemId, long quantity = 1) =>
        SlotContainer.CanAddItem(Items, itemId, quantity);

    public bool AddItem(string itemId, long quantity)
    {
        if (string.IsNullOrEmpty(itemId) || quantity <= 0) return false;

        if (!SlotContainer.AddItem(Items, itemId, quantity))
        {
            Debug.LogWarning($"[BankManager] Bank full — could not add {quantity}x {itemId}");
            return false;
        }

        Changed();
        return true;
    }

    public bool RemoveItem(string itemId, long quantity)
    {
        if (!SlotContainer.RemoveItem(Items, itemId, quantity)) return false;
        Changed();
        return true;
    }

    /// <summary>Reorders within the bank itself.</summary>
    public void SwapOrStackSlots(int fromIndex, int toIndex)
    {
        SlotContainer.SwapOrStackSlots(Items, fromIndex, toIndex);
        Changed();
    }

    // ── Transfers ─────────────────────────────────────────────────────────────

    /// <summary>Moves one inventory slot into a specific bank slot (drag and drop).</summary>
    public bool DepositSlot(int inventorySlot, int bankSlot)
    {
        if (!SlotContainer.Transfer(GameManager.Inventory?.Items, inventorySlot, Items, bankSlot))
            return false;

        Changed();
        GameEvents.FireInventoryChanged();
        return true;
    }

    /// <summary>Moves one bank slot into a specific inventory slot (drag and drop).</summary>
    public bool WithdrawSlot(int bankSlot, int inventorySlot)
    {
        if (!SlotContainer.Transfer(Items, bankSlot, GameManager.Inventory?.Items, inventorySlot))
            return false;

        Changed();
        GameEvents.FireInventoryChanged();
        return true;
    }

    /// <summary>Moves an entire inventory slot into the first available bank space.</summary>
    public bool Deposit(int inventorySlot, long quantity)
    {
        if (!SlotContainer.TransferQuantity(GameManager.Inventory?.Items, inventorySlot, Items, quantity))
            return false;

        Changed();
        GameEvents.FireInventoryChanged();
        return true;
    }

    /// <summary>Moves an entire bank slot into the first available inventory space.</summary>
    public bool Withdraw(int bankSlot, long quantity)
    {
        if (!SlotContainer.TransferQuantity(Items, bankSlot, GameManager.Inventory?.Items, quantity))
            return false;

        Changed();
        GameEvents.FireInventoryChanged();
        return true;
    }

    /// <summary>
    /// Banks everything the character is carrying. Stops at the first item that will
    /// not fit rather than silently discarding it.
    /// </summary>
    public int DepositAllItems()
    {
        var inv = GameManager.Inventory?.Items;
        if (inv == null) return 0;

        int moved = 0;
        for (int i = 0; i < inv.Count; i++)
        {
            if (SlotContainer.IsEmpty(inv[i])) continue;
            if (!SlotContainer.TransferQuantity(inv, i, Items, inv[i].quantity)) continue;
            moved++;
        }

        if (moved > 0)
        {
            Changed();
            GameEvents.FireInventoryChanged();
        }
        return moved;
    }

    // ── Coins ─────────────────────────────────────────────────────────────────

    /// <summary>Moves coins from the active character's wallet into the vault.</summary>
    public bool DepositCoins(long amount)
    {
        var account = AccountManager.Current;
        var ch      = CharacterManager.Current;
        if (account == null || ch == null || amount <= 0) return false;

        // Clamped by what the character has AND by what the vault can still hold, so
        // depositing into a near-full vault moves what fits instead of overflowing it.
        long moving = System.Math.Min(amount, ch.coins);
        moving      = System.Math.Min(moving, InventoryManager.MaxCoins - account.bankCoins);
        if (moving <= 0) return false;

        ch.coins          -= moving;
        account.bankCoins += moving;

        GameEvents.OnCoinsChanged?.Invoke(ch.coins);
        Changed();
        return true;
    }

    /// <summary>Moves coins from the vault back to the active character's wallet.</summary>
    public bool WithdrawCoins(long amount)
    {
        var account = AccountManager.Current;
        var ch      = CharacterManager.Current;
        if (account == null || ch == null || amount <= 0) return false;

        // Clamped by the vault's balance AND by room left in the wallet, mirroring
        // the deposit path — otherwise a withdrawal into a full wallet would be
        // deducted from the vault and clamped away on arrival.
        long moving = System.Math.Min(amount, account.bankCoins);
        moving      = System.Math.Min(moving, InventoryManager.MaxCoins - ch.coins);
        if (moving <= 0) return false;

        account.bankCoins -= moving;
        ch.coins          += moving;

        GameEvents.OnCoinsChanged?.Invoke(ch.coins);
        Changed();
        return true;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every mutation writes to disk immediately. The bank spans characters, so a
    /// crash between banking on one and withdrawing on another would otherwise
    /// duplicate or destroy material depending on which side was saved.
    /// </summary>
    private void Changed()
    {
        GameEvents.OnBankChanged?.Invoke();
        GameManager.Save?.Save();
    }
}
