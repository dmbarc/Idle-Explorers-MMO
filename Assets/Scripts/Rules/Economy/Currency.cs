using System;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// Which item ids are money, and which wallet they land in.
    ///
    /// ══ WHY GOLD IS NOT AN INVENTORY ITEM ═════════════════════════════════════════
    ///
    /// It was. `coins` is an entry in item_data.json, goblins drop 5-30 of them, and
    /// they occupied a bag slot like ore. That is fine in a game where the inventory
    /// is the whole model, and wrong in one where the server owns value:
    ///
    ///   * An inventory slot is a position in a grid a player rearranges. A balance is
    ///     a number with a history. Storing money as the former means the only form of
    ///     value in the game with no audit trail, sitting next to relic coins that are
    ///     bought with real money and have a complete one.
    ///
    ///   * Recipes now cost thousands of bars, so a 24-hour mining run fills the bag.
    ///     Gold competing for those slots means a player loses ore to loot they cannot
    ///     refuse, which is a worse outcome the more successfully they played.
    ///
    /// So loot that resolves to a currency is credited to the wallet and written to
    /// wallet_ledger, and never reaches an inventory slot.
    ///
    /// ══ WHY THE MAPPING IS SHARED ═════════════════════════════════════════════════
    ///
    /// The client still has to render a goblin dropping coins, and a drop the client
    /// puts in the bag while the server puts it in the wallet is a desync the player
    /// sees immediately and cannot explain. One answer, both sides.
    ///
    /// TODO(Phase 3): `coins` stays in item_data.json so existing loot tables, shop
    /// prices and merge recipes keep resolving. It is a currency that happens to have
    /// an item row, not an item. Content validation should eventually assert that no
    /// recipe takes one as an INPUT, since crafting consumes from the bag.
    /// </summary>
    public static class Currency
    {
        /// <summary>The in-game currency. Earned, spent, never bought.</summary>
        public const string Coins = "coins";

        /// <summary>The premium currency. Bought with real money, so it is audited hardest.</summary>
        public const string RelicCoins = "relic_coins";

        /// <summary>
        /// The item id a goblin's loot table uses for gold.
        ///
        /// Separate constant from <see cref="Coins"/> even though they are the same
        /// string, because they are different facts: one is a wallet column, the other
        /// is a row in item_data.json. They agree today and a content edit could
        /// separate them.
        /// </summary>
        public const string CoinsItemId = "coins";

        /// <summary>
        /// The item id a loot table uses for relic coins.
        ///
        /// ══ WHY A BOSS MAY DROP A PREMIUM CURRENCY ════════════════════════════
        ///
        /// Because it is the one place in the game that cannot be farmed on a timer.
        /// The King needs a thousand active kills to reach, has a five-minute enrage
        /// clock, and can only be beaten by actually being there — so a handful of
        /// relic coins from him is a reward for the hardest thing available rather
        /// than a tap somebody can leave running overnight.
        ///
        /// It goes through GrantItemAsync like any other drop, which means it lands in
        /// the WALLET and writes a ledger row rather than sitting in a bag slot. That
        /// matters: relic coins are the number this whole architecture was built to
        /// take off the player's machine, and an unledgered one would be unexplainable.
        /// </summary>
        public const string RelicCoinsItemId = "relic_coins";

        /// <summary>
        /// What a brand new account is given.
        ///
        /// ══ WHY IT IS HERE AND NOT A MIGRATION DEFAULT ════════════════════════
        ///
        /// A column default would silently apply to every account row ever inserted,
        /// including ones created by a test fixture or a repair script, and it would
        /// move the balance without a ledger row — which breaks the one invariant the
        /// ledger exists for.
        ///
        /// So it is a grant, made once, at the moment the account is created, with the
        /// row that explains it.
        /// </summary>
        public const long WelcomeRelicCoins = 1000L;

        /// <summary>The reason recorded against that grant, so it can be found later.</summary>
        public const string WelcomeReason = "welcome_grant";

        /// <summary>
        /// The wallet this item id is money for, or null if it is an ordinary item.
        ///
        /// Returning null rather than throwing: most items are not currency, and the
        /// caller asks about every single drop.
        /// </summary>
        public static string WalletFor(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;

            if (itemId == CoinsItemId)      return Coins;
            if (itemId == RelicCoinsItemId) return RelicCoins;

            return null;
        }

        /// <summary>True when this item id should be credited rather than stored.</summary>
        public static bool IsCurrency(string itemId) => WalletFor(itemId) != null;

        /// <summary>Whether a string names a wallet the schema actually has.</summary>
        public static bool IsWallet(string currency) =>
            currency == Coins || currency == RelicCoins;
    }
}
