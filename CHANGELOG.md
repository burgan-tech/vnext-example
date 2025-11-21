# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Initial project structure and configuration

### Changed

### Deprecated

### Removed

### Fixed

### Security

## [1.0.0] - 2024-12-19

### Added
- Initial release of @burgan-tech/vnext-template
- Domain-based project structure with template folders
- Core module with component access functions:
  - `getDomainConfig()` - Access domain configuration
  - `getSchemas()` - Load schema definitions
  - `getWorkflows()` - Load workflow definitions
  - `getTasks()` - Load task definitions
  - `getViews()` - Load view definitions
  - `getFunctions()` - Load function definitions
  - `getExtensions()` - Load extension definitions
  - `getAvailableTypes()` - Get available component types
  - `getDomainName()` - Get domain directory name
- JSON validation for all component files
- Comprehensive test suite with 10 test cases
- Package validation with 8 validation checks
- NPM package configuration with @burgan-tech scope
- GitHub Actions workflow for automated publishing
- Support for both NPM.js and GitHub Packages registries
- Semantic versioning with automated version calculation
- Complete documentation and README
- MIT license

### Features
- **Template Structure**: Ready-to-use project structure for vNext components
- **Component Loading**: Dynamic loading of JSON-based component definitions
- **Validation**: Built-in validation for JSON schemas and package structure
- **Publishing**: Automated CI/CD pipeline for package publishing
- **Scoped Package**: Published under @burgan-tech organization scope
- **Multi-Registry**: Support for publishing to NPM and GitHub Packages
- **Documentation**: Comprehensive README with usage examples and API documentation

### Technical Details
- Node.js 16+ compatibility
- JSON Schema validation support
- Domain-driven architecture pattern
- Modular component organization
- Cross-platform compatibility (Windows, macOS, Linux)
- GitHub Actions integration
- Automated testing and validation

### Package Contents
- Core module (`index.js`)
- Test suite (`test.js`)
- Validation script (`validate.js`)
- Domain template structure (`core/`)
- Configuration files (`package.json`, `vnext.config.json`)
- Documentation (`README.md`, `CHANGELOG.md`)
- CI/CD workflow (`.github/workflows/build-and-publish.yml`)

[Unreleased]: https://github.com/burgan-tech/vnext-template/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/burgan-tech/vnext-template/releases/tag/v1.0.0
