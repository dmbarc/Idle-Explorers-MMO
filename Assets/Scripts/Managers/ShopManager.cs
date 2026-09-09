using UnityEngine;

/// <summary>
/// Relic coins: the premium currency, the packs that grant it, and the things it buys.
///
/// ══ READ THIS BEFORE TAKING A SINGLE REAL PAYMENT ══════════════════════════════
///
/// Purchasing here is a STUB. It grants coins locally, immediately, for free, and
/// only in the Editor or a development build. No money changes hands and no store is
/// contacted. A release build refuses outright rather than presenting a shop that
/// looks real and is not.
///
/// Two things must exist before this can accept money, and neither is in the project:
///
///   1. Unity IAP wired to the store, with the product ids already declared in
///      shop_data.json registered in Google Play Console / App Store Connect.
///   2. SERVER-SIDE RECEIPT VALIDATION. relicCoins currently lives in account.json
///      in plain text on the player's own disk — a text editor is all it takes to
///      grant yourself a million. That is survivable for a single-player prototype
///      and completely unacceptable the moment a paying customer exists alongside
///      someone who did not pay. The balance has to become server-authoritative in
///      Phase 8, alongside the rest of the save.
///
/// PurchaseRelicCoins is deliberately the ONLY place a balance goes up from a pack,
/// so there is exactly one function to replace when that work happens.
/// </summary>
public class ShopManager : MonoBehaviour
{
    // ── Balance ───────────────────────────────────────────────────────────────

    public static long Balance => AccountManager.Current?.relicCoins ?? 0;

    /// <summary>Adds relic coins from any source and persists. Never takes a negative.</summary>
    public void Grant(long amount, string reason)
    {
        var account = AccountManager.Current;
        if (account == null || amount <= 0) return;

        // Saturating then clamped, like the coin wallet. A premium balance that
        // wrapped negative would read as a debt the player cannot clear.
        long total = SlotContainer.SafeAdd(account.relicCoins, amount);
        account.relicCoins = System.Math.Clamp(total, 0L, InventoryManager.MaxCoins);

        Debug.Log($"[Shop] +{amount} relic coins ({reason}). Balance {account.relicCoins}.");

        Changed();
    }

    /// <summary>Spends relic coins. Returns false and changes nothing if unaffordable.</summary>
    public bool TrySpend(long amount)
    {
        var account = AccountManager.Current;
        if (account == null || amount < 0) return false;
        if (account.relicCoins < amount) return false;

        account.relicCoins -= amount;
        Changed();
        return true;
    }

    private void Changed()
    {
        GameEvents.OnRelicCoinsChanged?.Invoke(Balance);
        GameManager.Save?.Save();
    }

    // ── Buying relic coins with money ─────────────────────────────────────────

    /// <summary>True when a purchase can be attempted at all.</summary>
    /// <summary>The flag the SERVER must have explicitly on. Mirrored here to hide the buttons.</summary>
    public const string TestGrantsFlag = "shop_test_grants";

    /// <summary>
    /// Whether the shop can hand out anything at all.
    ///
    /// ══ THE SERVER DECIDES, THIS ONLY HIDES BUTTONS ══════════════════════════
    ///
    /// Under a real server it is the flag, mirrored into the bootstrap payload, so the
    /// packs can be switched on for a playtest without a new build -- and switched off
    /// again in one statement. The client copy is courtesy: the endpoint checks the
    /// same flag again and refuses, so a player who edits this out gets a 403 rather
    /// than coins.
    ///
    /// Offline it is DevTools, as before, because there is nothing to ask.
    /// </summary>
    public static bool PurchasingAvailable =>
        IdleExplorers.Backend.ServerState.IsAuthoritative
            ? IdleExplorers.Backend.ServerState.FlagEnabled(TestGrantsFlag)
            : DevTools.Enabled;

    /// <summary>
    /// The single seam where real IAP lands.
    ///
    /// When Unity IAP is wired this becomes: start the store purchase, return
    /// immediately, and grant the coins from the purchase-completed callback ONLY
    /// after the server has validated the receipt. Nothing else in the game needs to
    /// change, because nothing else grants coins from a pack.
    /// </summary>
    public bool PurchaseRelicCoins(string packId)
    {
        var pack = GameManager.Content?.GetCoinPack(packId);
        if (pack == null)
        {
            Debug.LogWarning($"[Shop] No coin pack '{packId}'.");
            return false;
        }

        if (!PurchasingAvailable)
        {
            // Never pretend. A shop that appears to sell and silently does nothing is
            // worse than one that says it is not open yet.
            GameEvents.FireToast("Purchasing is not available yet.", ChatTone.Bad);
            Debug.LogWarning("[Shop] Purchase refused: no store is wired up. " +
                             "See the header of ShopManager.cs.");
            return false;
        }

        Debug.LogWarning($"[Shop] TEST GRANT — {pack.coins} relic coins for " +
                         $"{pack.DisplayPrice} without contacting any store. " +
                         "No money has changed hands.");

        // ══ THE SERVER GRANTS IT, NOT THIS ════════════════════════════════════
        //
        // Locally this used to write straight into account.json, which is the file the
        // player owns -- so the balance was both editable and, in a release build,
        // unreachable, and the shop simply refused to open.
        //
        // Granting server-side means the test coins live where the real ones will: one
        // balance, one ledger row, one authority. The only difference between this and
        // a purchase is that nobody paid, and the ledger says so.
        if (IdleExplorers.Backend.ServerState.IsAuthoritative)
        {
            _ = IdleExplorers.Backend.ServerState.GrantTestPackAsync(pack.id);
            return true;
        }

        Grant(pack.coins, $"stub purchase of '{pack.id}'");
        GameEvents.FireToast($"+{NumberFormatter.Format(pack.coins)} relic coins (test purchase)", ChatTone.Good);
        return true;
    }

    // ── Spending relic coins in game ──────────────────────────────────────────

    /// <summary>
    /// Buys a product with relic coins and puts it in the active character's bag.
    ///
    /// Inventory space is checked BEFORE the coins are taken. Charging a player and
    /// then discovering there is nowhere to put what they bought is the one failure
    /// mode a shop must never have.
    /// </summary>
    public bool Purchase(string productId)
    {
        var product = GameManager.Content?.GetShopProduct(productId);
        if (product == null)
        {
            Debug.LogWarning($"[Shop] No product '{productId}'.");
            return false;
        }

        // ══ THE SERVER TAKES THE MONEY WHEN THERE IS ONE ══════════════════════
        //
        // Everything below spends relic coins out of the local account and puts the
        // item in the local bag. Under an authoritative server both are fiction: the
        // next pull restores the coins and takes the item away again.
        //
        // What the player saw was a purchase that appeared to work and an item that
        // could not then be equipped -- with no error, because nothing had failed.
        // The client had simply been talking to itself.
        if (IdleExplorers.Backend.ServerState.IsAuthoritative)
        {
            _ = IdleExplorers.Backend.ServerState.BuyProductAsync(productId);
            return true;
        }

        var inventory = GameManager.Inventory;
        if (inventory == null)
        {
            GameEvents.FireToast("Log in to a character first.", ChatTone.Bad);
            return false;
        }

        if (!inventory.CanAddItem(product.itemId))
        {
            GameEvents.FireToast("No room in your inventory.", ChatTone.Bad);
            return false;
        }

        // ══ WHICH PURSE, OFFLINE ══════════════════════════════════════════════
        //
        // The server picks the wallet from ShopProduct.Currency in the shared rules.
        // This is the editor and offline path, and it has to pick the same one — a
        // gold potion that quietly took relic coins here would be a divergence the
        // player only notices once they have lost some.
        if (product.goldCost > 0L)
        {
            if (!inventory.TrySpendCoins(product.goldCost))
            {
                long owing = System.Math.Max(0L, product.goldCost - inventory.Coins);
                GameEvents.FireToast($"Need {NumberFormatter.Format(owing)} more gold.", ChatTone.Bad);
                return false;
            }
        }
        else
        {
            if (Balance < product.relicCoinCost)
            {
                long short_ = product.relicCoinCost - Balance;
                GameEvents.FireToast($"Need {NumberFormatter.Format(short_)} more relic coins.", ChatTone.Bad);
                return false;
            }

            if (!TrySpend(product.relicCoinCost)) return false;
        }

        inventory.AddItem(product.itemId, System.Math.Max(1L, product.quantity));

        var item = GameManager.Content?.GetItem(product.itemId);
        GameManager.Audio?.PlayPurchase();
        GameEvents.FireToast($"Bought {item?.DisplayName ?? product.DisplayName}.", ChatTone.Good);
        Debug.Log($"[Shop] Bought '{product.id}' for {product.relicCoinCost} relic coins.");
        return true;
    }
}
