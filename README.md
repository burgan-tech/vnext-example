# core

A structured template package for vNext workflow components with domain-based architecture. This package provides a foundation for building scalable workflow systems with schemas, tasks, views, functions, and extensions.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

## 🚀 Features

- **Domain-Driven Architecture**: Organized by business domains with clear separation of concerns
- **Component-Based Structure**: Modular design with schemas, workflows, tasks, views, functions, and extensions
- **JSON Schema Validation**: Built-in validation for all component definitions
- **Template System**: Ready-to-use template structure for new vNext projects
- **Type Safety**: Structured exports with clear APIs for accessing components

## 📦 Installation

### Install as dependency
```bash
npm install @my-organization/core
```

### Install as dev dependency
```bash
npm install --save-dev @my-organization/core
```

## 🏗️ Project Structure

```
vnext-template/
├── core/              # Domain-specific components
│   ├── Extensions/            # Extension definitions
│   ├── Functions/             # Function definitions
│   ├── Schemas/              # JSON schema definitions
│   ├── Tasks/                # Task definitions
│   ├── Views/                # View components
│   └── Workflows/            # Workflow definitions
├── index.js                  # Main entry point
├── vnext.config.json         # Domain configuration
└── package.json              # Package metadata
```

## 🏛️ Architecture Principles

### Component Types

1. **Schemas**: JSON Schema definitions for data validation
2. **Workflows**: Business process definitions and state machines
3. **Tasks**: Individual task definitions and configurations
4. **Views**: User interface and presentation components
5. **Functions**: Reusable business logic functions
6. **Extensions**: Plugin and extension definitions

## 🛠️ Development

### Running Tests
```bash
npm test
```

### Test Scenarios

See **[TEST-SCENARIOS.md](TEST-SCENARIOS.md)** — the index of every behaviour scenario in this repo:
which vNext feature set each one covers, why it was added, how to run its integration and load
tests, and the known coverage gaps. Add a row there in the same commit as any new scenario.

## ⚙️ Configuration

The `vnext.config.json` file allows you to customize paths and exports:

```json
{
  "domain": "my-domain",
  "paths": {
    "componentsRoot": "my-domain",
    "schemas": "Schemas",
    "workflows": "Workflows",
    "tasks": "Tasks",
    "views": "Views",
    "functions": "Functions",
    "extensions": "Extensions"
  },
  "exports": {
    "schemas": ["schema1.json", "schema2.json"],
    "workflows": ["workflow1.json"],
    "tasks": [],
    "views": [],
    "functions": [],
    "extensions": []
  }
}
```

### Path Configuration

You can customize component directory names:

```json
{
  "paths": {
    "componentsRoot": "src",
    "workflows": "Flows",
    "schemas": "Models"
  }
}
```

## ✅ Validation

Validate your project structure and schemas:

```bash
npm run validate
```

This will check:
- Package.json structure and content
- Main entry point functionality
- vnext.config.json validation
- Domain directory structure
- JSON file syntax validation
- Schema validation using @burgan-tech/vnext-schema
- Module functionality
- Semantic versioning compliance

### Validation Output

The validation provides detailed output with:
- ✅ Passed validations
- ❌ Failed validations with file paths and line numbers
- 📊 Summary statistics
- 📋 Failed files summary for easy navigation

## 🏗️ Build

Build your domain package for deployment or cross-domain usage:

```bash
# Runtime build (default) - Complete domain structure
npm run build

# Reference build - Only exported components
npm run build:reference

# Runtime build explicitly
npm run build:runtime
```

### Build Options

```bash
npm run build -- [options]

Options:
  -o, --output <dir>     Output directory (default: dist)
  -t, --type <type>      Build type: reference or runtime (default: runtime)
  --skip-validation      Skip validation during build
  -h, --help             Show help message
```

### Build Types

| Type | Description | Use Case |
|------|-------------|----------|
| `runtime` | Complete domain structure with all files | Engine deployment |
| `reference` | Only exported components from vnext.config.json | Cross-domain usage |

### Examples

```bash
# Build to custom directory
npm run build -- -o my-build

# Reference build to custom directory
npm run build -- -t reference -o packages/ref

# Skip validation for faster builds
npm run build -- --skip-validation
```

### Build Output Structure

**Runtime Build:**
```
dist/
├── <domain>/
│   ├── Extensions/
│   ├── Functions/
│   ├── Schemas/
│   ├── Tasks/
│   ├── Views/
│   └── Workflows/
├── vnext.config.json
├── package.json
├── README.md
└── LICENSE
```

**Reference Build:**
```
dist/
├── <domain>/
│   ├── Extensions/     # Only exported files
│   ├── Functions/      # Only exported files
│   ├── Schemas/        # Only exported files
│   ├── Tasks/          # Only exported files
│   ├── Views/          # Only exported files
│   └── Workflows/      # Only exported files
├── vnext.config.json
├── package.json
├── README.md
└── LICENSE
```

## 📜 Available Scripts

| Script | Description |
|--------|-------------|
| `npm run validate` | Validate project structure and schemas |
| `npm run build` | Build runtime package to dist/ |
| `npm run build:runtime` | Build runtime package explicitly |
| `npm run build:reference` | Build reference package with exports only |
| `npm run setup <name>` | Setup domain with given name |
| `npm run sync-schema` | Sync schema version from dependencies |
| `npm test` | Run tests |


## 🧪 Load Testing (JMeter)

Asynchronous (`sync=false`) load test for the account-opening workflow is executed via `docker-compose.test.yml`. JMeter joins the existing `bbt-development` Docker network and does not modify the main `docker-compose.yml`.

### Structure

```
jmeter/
├── tests/
│   └── workflow-test.jmx
└── results/              # gitignored; reports are written here
```

### Running

```bash
# Default: 10 users, 10s ramp-up, 5 loops, base URL http://host.docker.internal:4201
docker compose -f docker-compose.test.yml up --abort-on-container-exit

# Custom base URL (e.g. a service name on the same network)
VNEXT_BASE_URL=http://amorphie-workflow:4201 \
  docker compose -f docker-compose.test.yml up --abort-on-container-exit

# Custom load profile
VNEXT_BASE_URL=http://host.docker.internal:4201 \
JMETER_USERS=50 JMETER_RAMPUP=30 JMETER_LOOPS=10 \
  docker compose -f docker-compose.test.yml up --abort-on-container-exit

# Tear down
docker compose -f docker-compose.test.yml down
```

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `VNEXT_BASE_URL` | `http://host.docker.internal:4201` | vNext workflow engine base URL |
| `JMETER_USERS` | `10` | Number of concurrent threads (users) |
| `JMETER_RAMPUP` | `10` | Ramp-up time in seconds |
| `JMETER_LOOPS` | `5` | Iterations per thread |

### Scenario

Single thread group, full happy path:

1. `POST /instances/start?sync=false` → extract `id` → poll state until `status=A` (max 5 polls, 1s backoff while `B`)
2. `PATCH /transitions/select-demand-deposit?sync=false` → poll until `status=A`
3. `PATCH /transitions/submit-account-details?sync=false` → poll until `status=A`
4. `PATCH /transitions/confirm-account-opening?sync=false` (final transition; response not asserted)

If the polled status returns `F` (or unexpected `C`) at any intermediate step, the remaining samplers in that iteration are skipped and the next loop starts.

### Results

- Raw JTL: `./jmeter/results/result.jtl`
- HTML report: `./jmeter/results/html-report/index.html`

## 🤝 Contributing

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add some amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🏢 About Burgan Tech

This package is maintained by the Burgan Tech team as part of our commitment to building scalable, domain-driven workflow solutions.

## 🔗 Links

- [NPM Package](https://www.npmjs.com/package/@burgan-tech/vnext-example)
- [GitHub Repository](https://github.com/burgan-tech/vnext-example)
- [Issues](https://github.com/burgan-tech/vnext-example/issues)
- [Documentation](https://github.com/burgan-tech/vnext-example#readme)

## 📞 Support

For support and questions:
- Create an issue on [GitHub](https://github.com/burgan-tech/vnext-example/issues)
- Contact the development team at dev@burgan-tech.com

---

Made with ❤️ by the Burgan Tech team