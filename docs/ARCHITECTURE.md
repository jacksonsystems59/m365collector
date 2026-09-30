# Architecture — 0.0.2

## Components and dependency direction

Contracts defines customers, tenant identity, role names, module manifests, `ICollectorModule`, `ICustomerRepository`, app-only readers and short-lived bootstrap sessions. Core depends on Contracts and provides runtime paths, JSON configuration, service control, release checks and package validation. Storage depends on Core/Contracts and owns SQLite. Security depends on Storage and separates password authentication from capability authorization; it also owns Windows ACLs and certificates. Entra depends on Security/Contracts for MSAL and Graph. TenantIdentity implements the collector contract through the app-only reader. Service hosts scheduling and verification. GUI orchestrates setup and the technician experience. Updater is a separate executable; GUI references its request DTO. There are no circular project references.

All components, executable metadata and the release manifest are 0.0.2. The SDK target is net10.0-windows. Releases publish separate self-contained x64 component directories, so the updater can be copied out of the application directory before replacing binaries.

## Runtime layout

Application binaries live at `%ProgramFiles%\M365Collector`. The machine locator is `%ProgramData%\M365Collector.Installation\installation.json`, protected against ordinary-user changes. The completed flag is written only after service/database verification and creation of the local Administrator. An incomplete locator permits setup resumption. Malformed locators fail closed instead of triggering a destructive reset.

The technician chooses a dedicated local DataRoot. Network paths, drive roots, reparse-point ancestors and overlap with the application directory are rejected. Setup checks free space and actual file creation. It creates:

```text
DataRoot/
  Config/
  Database/collector.db
  Logs/Updates/
  Reports/
  Exports/
  Cache/
  Temp/
  Service/heartbeat.json
  Customers/<tenant-guid>/
    Data/tenant-identity.json
    Audit/
    Reports/
    Exports/
    Cache/
```

GUID-derived customer paths prevent customer-name path traversal and accidental cross-tenant output. The machine's administrators and the collector service can access all tenants; this is filesystem separation, not a hostile multitenant sandbox.

## Storage and service

`CollectorStore` uses parameterized SQLite commands, short-lived nonpooled connections, a busy timeout and foreign keys. Migration 1 creates Settings, Users, Customers (including serialized tenant metadata), ModuleConfiguration, CollectionState, ConnectionRequests, UpdateHistory and SchemaVersion. Migrations are transactional and idempotent. An older application cannot migrate a newer schema. Online SQLite backup creates a consistent database snapshot before updates.

The service runs as LocalService with an unrestricted service SID (the Windows SID mechanism, not unrestricted filesystem access). ACLs grant that SID the required data/key access; no service password is stored. SCM startup is automatic with restart recovery. Startup verifies database integrity; every five seconds, successful schema access produces an atomic heartbeat containing version, DataRoot, timestamp and schema. GUI shutdown has no effect on the service.

The service processes pending verification/collection requests and performs scheduled Tenant Identity collection hourly for enabled customers. A request produces Verified or Failed; only a matching Verified tenant identity can be saved as a customer. The service accesses the actual machine private key and acquires an app-only token, so the workflow tests the service identity rather than the elevated GUI identity. Heartbeats run independently from potentially slow network collection.

## Module boundary

Manifests declare ID, display name, version, APIs, exact Microsoft application permissions, PowerShell dependencies and collection capability. Tenant Identity requires only Microsoft Graph application `Organization.Read.All`; it needs no PowerShell modules. Sign-in, Directory Changes and Unified Audit implement IAuditCollectorModule and persist StoredEvent records via IAuditStore. CollectorCatalog declares API permissions and defaults. Exchange message trace remains a future module.

## Deliberate foundation limits

Local roles apply within the GUI; Windows administrators are the machine trust boundary. The GUI requires elevation in this first release. There is no network listener or web portal. No mail, files, sign-in activity or Teams data is collected. Automatic certificate renewal, certificate cleanup for abandoned attempts, deletion of customer apps, advanced scheduler controls and retention policies are future work. Monitor certificate expiry and renew manually before expiry.

## 0.0.2 collection architecture

Schema 2 adds AuditEvents, CollectorRuns, ModuleHealth, CollectorCheckpoints and ProcessedAuditContent. Tenant/module/provider ID keys isolate and deduplicate records. A bounded four-job scheduler runs independent collectors; page commits precede checkpoint advancement. SQLite WAL supports concurrent collection and snapshot CSV reads. Module cards and Audit Explorer use the local store. See [collector details](COLLECTOR-MODULES.md).
