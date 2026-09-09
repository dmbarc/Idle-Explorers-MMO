-- A way to test the shop without a payment processor.
--
-- ══ WHY THE ROW EXISTS AND IS FALSE ═══════════════════════════════════════════
--
-- FeatureFlags treats an UNKNOWN flag as on, which is right for gameplay: a missing
-- row should not switch the game off mid-playtest. It is exactly wrong for a switch
-- that mints premium currency, where a missing row would be a tap nobody opened.
--
-- The endpoint therefore asks IsExplicitlyEnabledAsync, which requires the row to
-- exist and be true. Seeding it false makes the flag KNOWN and OFF -- visible in the
-- bootstrap payload, listed in the table, and switchable in one statement:
--
--     update feature_flag set enabled = true where flag = 'shop_test_grants';
--
-- ══ WHAT IT DOES AND DOES NOT ALLOW ═══════════════════════════════════════════
--
-- It grants a coin pack's relic coins WITHOUT a payment, so the shop and everything
-- downstream of a balance can be exercised. It is not a purchase: no store is
-- contacted, no receipt exists, and every grant lands in wallet_ledger with a reason
-- that says so, which is what makes the test coins findable and removable when real
-- purchasing arrives.
--
-- Turning this on in front of paying players would let anybody mint the thing other
-- people paid for. It is off, and it is off by default in the strongest sense the
-- flag system can express.
insert into feature_flag (flag, enabled)
values ('shop_test_grants', false)
on conflict (flag) do nothing;

comment on table feature_flag is
    'Switches read by the API and mirrored to the client. Unknown flags are ON for '
    'gameplay features and OFF for anything that grants value -- see '
    'FeatureFlags.IsExplicitlyEnabledAsync.';
