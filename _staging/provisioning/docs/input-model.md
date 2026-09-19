# Provisioning input model

## Constitutional boundary

The runtime constitution assigns capability realization to the runtime (§7) and requires Institutions to remain independent of execution mechanisms (§10). An Organization derives its authority from its Institution; neither is directly coupled to a storage, identity, or messaging technology.

Source: [runtime institution specification](https://github.com/aetheric-forge/aetheric-runtime/blob/main/docs/specs/institution.md). The concrete fixture below is pinned separately for reproducible planning.

## Three inputs to planning

| Input | Responsibility | Examples |
| --- | --- | --- |
| Institution definition | Purpose, capabilities, abstract resource categories, ownership, dependencies, and policy | A staging workspace owned by Decisions; a parent-owned knowledge resource |
| Deployment bindings | Realize those requirements in a particular deployment | Workbench with Redis backing; a parent capability resolution path |
| Parent context | Resolve and verify capabilities supplied by the enclosing institutional scope | Available `IArchive`, `ILibrary`, `IPostOffice`, and `IRegistrar` implementations |

The engine combines all three before selecting concrete provider operations. It must not infer technology from an Institution or Organization name, a role, or an abstract resource category alone. An override changes deployment configuration; it does not rewrite institutional meaning or ownership.

The reviewable plan distinguishes owned-resource actions, inherited-capability checks, missing inputs, and conflicts. Parent resolution paths identify where a dependency should be found; a path string alone is not proof that a usable capability exists.

## First concrete fixture: Decisions Office

Source commit: `4e398147f9a7cd8ba47258a8e416f3e86f404a93` in ADR Campus.

- [Institution definition](https://github.com/aetheric-forge/adr-campus/blob/4e398147f9a7cd8ba47258a8e416f3e86f404a93/institution/decisions-institution.yaml)
- [Deployment bindings](https://github.com/aetheric-forge/adr-campus/blob/4e398147f9a7cd8ba47258a8e416f3e86f404a93/institution/decisions-institution.bindings.yaml)

These are declarative YAML artifacts. Reading them does not require building the repository or loading its plugin assembly. The definition is shaped around `IInstitutionTemplate`, with additional IDs, ownership, and dependency information. The artifact explicitly notes that the runtime does not yet provide a YAML loader.

| Requirement | Ownership / source | Supplied binding | Planner responsibility |
| --- | --- | --- | --- |
| `draft-workspace` | Owned; `staging` category | `workbench`, stage `adr-campus-workbench`, Redis backing | Plan only the workspace operations defined by the agreed deployment contract |
| `decision-record` | Parent; `knowledge` category | Realized through Archive and Library dependencies | Validate inherited access; do not create a separate provider resource |
| `IArchive` | Parent dependency | `campus.archive` | Resolve and verify |
| `ILibrary` | Parent dependency | `campus.library` | Resolve and verify |
| `IPostOffice` | Parent dependency | `campus.postoffice` | Resolve and verify |
| `IRegistrar` | Parent dependency | `campus.registrar` | Resolve and verify |

The bindings include an in-memory fallback when no Redis connection is configured. The plan must make that durability choice visible. Connection failures must not silently change an approved Redis-backed plan to in-memory execution.

No MongoDB database, S3 bucket, or Keycloak client is requested for creation by this child definition. Those technologies may realize parent capabilities in another deployment, but choosing and provisioning them requires that owner's definition and deployment bindings.

## Validation and unresolved integration details

- Validate stable IDs and cross-references; display-name changes must not change resource identity.
- Require matching institution identity and compatible versions across definition and bindings. Agree a schema-version policy separately from institution version `1.0.0`.
- Honor all declared dependencies. Although the source comments allow a generic loader to ignore the extension, the provisioner cannot safely declare readiness while required capabilities are unresolved.
- Reject unknown ownership or unsupported binding semantics with useful errors.
- Define the parent-context representation and live capability checks before claiming end-to-end readiness.
- Define what the Workbench/Redis binding actually allocates or configures, including access credentials and compatible existing state. Do not assume a `stage` string implies a new Redis server, database, or user.
- Agree how exported configuration is consumed by the mounted Decisions institution. The YAML artifact alone does not implement that integration.

These decisions keep M0 open. They do not require replacing the supplied institutional definition with a provider-specific schema.

## M2 implementation decisions

The provisioner now supports the supplied YAML shape through its Definitions library; this does not add a YAML loader to `aetheric-runtime`. The profile is documented in [M2 loading and review](m2-loading-review.md). Institution version and schema/profile version remain distinct.

For review, a parent context has an identity, revision, and an explicit contract-to-source catalog. All three contribute to plan identity. Missing or mismatched entries block review. The current host marks this catalog as simulated and does not treat it as proof of live infrastructure access. The `IParentCapabilityResolver` remains the seam for live verification in M3.
