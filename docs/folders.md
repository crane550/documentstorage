# Folder management

Open **Folders** or visit `/folders`. The directory includes every connected storage root and its folders, with path search, a storage filter and pages of 40 entries. Select a folder to see its children, ancestors, saved guidance and available actions. The edit button beside a sidebar folder opens the same tools. **Open in Documents** returns to document browsing and guidance/intake editing.

**New subfolder** creates a real directory under the selected folder. **Rename folder** changes its name while keeping its parent. **Move folder** keeps its name and selects a destination in the same storage location; the form shows the old and new paths. The destination picker supports filtering and excludes the source, its descendants and its current parent. All contents move together. Before and after a move, content verification covers up to 10,000 entries and 10 GB (including folder contents); larger operations must be split into smaller subfolders. Existing names are never overwritten and portable Windows/NAS filename rules apply on every server.

Storage roots remain fixed; create and rearrange their children. Offline roots are visible but cannot be edited. Cross-storage transfers are deferred until a verified copy/recovery contract is implemented. Moving a subtree that contains intake into another intake scope is rejected to preserve non-overlapping intake folders.

Folder and document IDs, saved instructions and intake flags survive app-managed renames/moves. Rules targeting these folder IDs continue to refer to them. Moves use the existing journal and recovery gate; unresolved operations block further writes in that storage. Open [Recovery](recovery.md) after an interrupted operation; paused storage is identified throughout the app. External Windows Explorer renames still have the reconciliation limitation described in the README.

Creating a child also checks that its parent still exists on disk, so a folder removed outside the app is not silently recreated.

Create, rename and move forms capture their source/parent paths. The writer rechecks them under its lock, so another tab cannot silently redirect a stale form after a folder moves. Validation errors keep the form open. Cancel and Escape close it without changing storage. Refresh the page to reload after a stale-form error. Moves can change inherited guidance and whether ordinary documents are inside intake; inspect the displayed destination before saving.

Verification: `node tests/browser/folders.mjs` drives a disposable loopback app (default port 5242), checks real directory changes, descendants, collision/invalid-name rejection, preserved guidance, competing tabs, direct navigation, sidebar access and mobile layout. The Core checks also cover stale source/destination/parent bindings and intake nesting, in addition to existing journal recovery checks. Windows service permissions and actual NAS availability still require server verification.
