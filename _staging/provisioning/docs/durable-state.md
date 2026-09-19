# Local durable state and restart recovery

The Persistence library supplies `FileRunStateStore` and `EncryptedFileSecretStore` without adding filesystem or cryptography dependencies to the engine. Hosts opt in by passing these stores to `ProvisioningEngine`:

```csharp
var state = new FileRunStateStore(runDirectory);
var secrets = new EncryptedFileSecretStore(secretDirectory, encryptionKey);
var engine = new ProvisioningEngine(providers, parentResolver, state, secrets);
```

`encryptionKey` is a host-supplied 32-byte AES key. Keep it outside the state directory, supply the same key on restart, and back it up with appropriate access controls. The library never generates a replacement key or rotates credentials. The Blazor simulation remains in memory: persisting its completion records without persisting its simulated resources would misrepresent what exists.

## Recovery contract

- Versioned JSON checkpoints contain outcome codes and opaque secret references, not secret values. Writes flush a temporary file before atomically replacing the checkpoint in the same directory. An interrupted temporary file is ignored; invalid checkpoints fail closed.
- AES-256-GCM encrypts secret values with random nonces and authenticates their reference IDs. The scope/name pair deterministically identifies a reference. A separate lock serializes create-or-reuse across store instances and processes. A wrong key or damaged ciphertext cannot silently trigger credential replacement.
- The engine holds a per-plan file lease over its full read/execute/checkpoint cycle. Waiting supports cancellation; process exit releases the lease. Lock files remain and must not be deleted while any host is running. Direct `SaveAsync` callers must hold the execution lease if performing a read/modify/write cycle.
- Parent capabilities are checked again on every attempt. Successful owned actions survive intervening parent failures and cancellations. Before skipping a completed action, the engine verifies that its referenced credentials remain readable.
- If the provider succeeds but the checkpoint is not saved, retry calls the provider again with the same credential. Providers must reconcile compatible existing resources idempotently; checkpointing does not guarantee exactly-once external side effects.
- Reconstruct the same plan from the same inputs after restart. This does not persist review approval, source documents, or executable plans.

## Deployment boundary

Use private, host-controlled directories on a local filesystem supporting atomic replacement and .NET file sharing locks. New Unix directories/files use owner-only modes; existing directories and Windows ACLs are the host's responsibility. The store assumes trusted local writers. It is not a distributed lock or an untrusted-file ingestion boundary. Different plan IDs have different leases, so provider-side conflict checks remain necessary for overlapping resources.

These tests establish process-restart recovery, not sudden-power-loss durability: the containing directory is not explicitly fsynced after rename. Disk loss, missing keys, and damaged records require operator recovery from consistent backups. Network filesystem semantics, key rotation, managed-secret integrations, resource drift checks, and real-provider integration are separate work.

## Evidence

`dotnet test --configuration Release` includes fresh-engine recovery, credential reuse, completed-work skipping, parent rechecks, cancellation/resume, competing engine instances, cancellable leases, concurrent secret creation, corruption/wrong-key rejection, and interrupted checkpoint handling.

Two process-level cases launch the harness in separate .NET processes: transient failure/retry/repeat and abrupt exit after resource creation but before checkpoint/retry/repeat. They verify stable credential fingerprints, resource reconciliation, and release of the exited process's run lock. The harness's file-backed resource is only an external-provider test double. Its `PROVISIONING_TEST_KEY` environment variable is test plumbing, not a production key-management recommendation.

This advances the M3/M4 persistence foundation. Live-provider and full v0.1 release gates remain open.
