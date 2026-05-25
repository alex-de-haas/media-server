# File and Directory Management

## Description

Media Server manages files and directories across one or more configured
storage roots. Every file operation must remain constrained to those roots and
must reject directory traversal attempts.

## Storage Roots

Media Server supports attaching multiple physical directories as storage roots.
Each root has:

- Unique ID.
- Display name.
- Absolute physical path.
- Read and write permissions.
- Free and total space.

Example storage root:

```json
{
  "id": "{uuid}",
  "name": "Movies Disk",
  "path": "/mnt/media/movies"
}
```

## File Operations

Supported operations:

- Upload files.
- Copy files.
- Move files.
- Delete files.
- Rename files.

Constraints:

- Operations are restricted to attached storage roots.
- Atomic operations should be used where the operating system supports them.
- Large files must be handled with streams.
- Multiple-file operations should be supported where practical.

## Directory Operations

Supported operations:

- Create directory.
- Copy directory recursively.
- Move directory.
- Delete directory recursively.
- Rename directory.

Additional behavior:

- Long operations report progress through SignalR.
- All paths are validated against directory traversal attacks.

## API Endpoints

Example internal endpoints:

```text
GET    /api/files?path=/movies
POST   /api/files/upload
POST   /api/files/copy
POST   /api/files/move
DELETE /api/files
POST   /api/directories
```

## Testing Expectations

Backend tests should use xUnit and Imposter.

Required coverage:

- Storage root resolution.
- Path sandboxing and traversal rejection.
- Copy, move, delete, rename, and create-directory behavior.
- Long-running operation progress reporting.
