# SH-T06 Publication and events

- **Owner:** __________
- **Directories:** `src/Sheltering/`, `src/Routing/` (publication contract), `contracts/events/`, optional Compose profile
- **Technologies:** .NET application handlers, transactional outbox, scripted event replay, RabbitMQ in Stage B
- **Baseline:** [06 Management events](../06-management-events-connectivity.md) and [04 route review](../04-dispatch-openrouteservice.md).

## What the demo shows

A committed reservation appears as pending publication until Routing approves the instruction. After a closure, a replacement goes out with a newer version. Replaying the old event does not restore the old instruction.

## Scope

### Stage A (demo)

- [ ] Make destination publication a separate step after successful booking. Wire the coordinator workflow to Routing's existing propose, publish and withdraw operations.
- [ ] Read both owners on retries and handle missing publication, changed route evidence and stale assignment revisions. Preserve the receipt key for a repeated command.
- [ ] Add event envelopes and replay fixtures for the new group, site, allocation and admission facts. Verify the SH-W5 instruction state without a live broker.

### Stage B (completion)

- [ ] Connect the owner outboxes to the shared RabbitMQ topic exchange under the optional `full` profile. Add consumer deduplication and reconciliation against authoritative versions.
- [ ] Test restart after commit, broker redelivery, and reordering across multiple events at the same subject revision. Count only committed facts in operational views.

## Dependencies and verification

Receive SH-W3 and SH-W4 and coordinate event payloads with each owner. Deliver current, pending and withdrawn instruction examples for SH-W5. Ensure a capacity hold is distinct from an issued route and that publishing fails safely when current eligibility cannot be validated. Cover SH-A06 and SH-A07.

## Results

_(Fill in publication failure, replay, reconciliation and versioning results.)_
