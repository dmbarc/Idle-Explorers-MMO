-- Turn the unpaid relic-coin tap off, and mean it.
--
-- ══ WHY A SECOND MIGRATION AND NOT AN EDIT TO THE FIRST ═══════════════════════
--
-- The original seeds the row with ON CONFLICT DO NOTHING, which is correct for a
-- seed: it must not stamp on an operator who has deliberately flipped the switch.
--
-- The consequence is that it cannot turn the switch back OFF. A database where
-- somebody ran the one-line update in that file's comment stays on through every
-- future `db push`, because the seed politely does nothing.
--
-- So this is an explicit statement of intent rather than a seed, and it is a new file
-- because migrations that have already run do not run again.
--
-- ══ WHAT THIS DOES NOT TOUCH ══════════════════════════════════════════════════
--
-- Real purchasing, which has never existed. ShopManager.PurchasingAvailable is false
-- and stays false until there is store registration and server-side receipt
-- validation -- see the standing constraint in ROADMAP.md.
--
-- New accounts still receive their welcome relic coins. That is a GRANT made once at
-- account creation with a ledger row explaining it, not a tap anybody can pull, and
-- it goes through Caller.AccountIdAsync rather than through this flag.
update feature_flag
   set enabled = false
 where flag = 'shop_test_grants';

-- And make sure the row exists at all, for a database that predates the seed.
insert into feature_flag (flag, enabled)
values ('shop_test_grants', false)
on conflict (flag) do nothing;
