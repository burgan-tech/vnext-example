# Pseudo UI View Author Guide

> **Audience**: People who write view JSON — Forge ViewDesigner users, BFF/backend teams, AI co-author skills.
> **Goal**: Rules for correct `view.json` + `schema.json` structure, action vocabulary, expression namespace, common patterns.

This document captures the "how" decisions for view-driven UI design. It is not a component reference — it is a **pattern guide**.

---

## 1. Core mental model

```
Screen = f(schema, view, data)
```

- **schema.json** = data contract + business rules (field type, validation, LOV, conditional, multi-lang labels)
- **view.json** = visual arrangement (component tree, bind targets, actions)
- **data** = comes from runtime (`formData` filled by the user, `instanceData` enriched by BFF)

The two files are **designed independently**: the backend/analyst writes the schema, the UI/UX designer writes the view. Validation/LOV/lookup defined in the schema **runs automatically** in the view — the view author never repeats it.

---

## 2. Schema authoring — what goes where?

### 2.1 Field type + label

```json
{
  "firstName": {
    "type": "string",
    "minLength": 2,
    "x-labels": { "en": "First name", "tr": "Ad" },
    "x-errorMessages": {
      "required": { "en": "Required", "tr": "Zorunlu" },
      "minLength": { "en": "Too short", "tr": "Çok kısa" }
    }
  }
}
```

- `type` + standard JSON Schema keywords (minLength/pattern/min/max/format)
- `x-labels` — multi-lang field label (auto-rendered on inputs)
- `x-errorMessages` — per-rule custom error message (`required`, `minLength`, `pattern`, `min`, `max`, `format`)

### 2.2 Enum dropdown

```json
{
  "currency": {
    "type": "string",
    "enum": ["TRY", "USD", "EUR"],
    "x-enum": {
      "TRY": { "en": "Turkish Lira",  "tr": "Türk Lirası" },
      "USD": { "en": "US Dollar",     "tr": "ABD Doları" },
      "EUR": { "en": "Euro",          "tr": "Euro" }
    }
  }
}
```

The SDK auto-binds when rendering a Dropdown. In the view just write: `{ "type": "Dropdown", "bind": "currency" }`.

### 2.3 Remote dropdown (LOV)

```json
{
  "city": {
    "type": "string",
    "x-lov": {
      "source": "urn:vnext:fn:get:shared:get-cities",
      "valueField": "$.response.data[*].code",
      "displayField": "$.response.data[*].name",
      "filter": [
        { "param": "countryCode", "value": "$form.country", "required": true }
      ]
    }
  }
}
```

- `source`: URN/URL (host resolver will resolve it)
- `valueField`, `displayField`: JsonPath (array path + item key)
- `filter[].value`: `$form.x`, `$instance.x`, `$param.x`, or a constant
- `required: true` → if the param can't be resolved, the LOV doesn't load (cascade is cleared)

Cascade: when `country` changes, the `city` LOV is auto-refreshed and the previous selection is reset.

### 2.4 Read-only enrichment (Lookup)

**Critical naming rule** (`view-model-vocabulary.json`): lookup results are accessed via **`$lookup.{propertyName}.{field}`**, where `{propertyName}` is the **schema property that owns the `x-lookup`**. So to read `$lookup.branchDetail.*`, the `x-lookup` must live on a property literally named `branchDetail` — **not** on the `branchCode` input field. Define the lookup as its own read-only property; don't attach it to the input it enriches.

In schema — a dedicated read-only property (no `bind` target; not in `required`):
```json
{
  "branchCode": {
    "type": "string",
    "x-lov": { "source": "urn:vnext:fn:get:core:get-branches", "valueField": "$.data[*].code", "displayField": "$.data[*].name" }
  },
  "branchDetail": {
    "type": "object",
    "description": "Read-only enrichment looked up from branchCode (display only).",
    "additionalProperties": true,
    "x-lookup": {
      "source": "urn:vnext:fn:get:core:get-branch-detail",
      "resultField": "$.data",
      "filter": [{ "param": "code", "value": "$form.branchCode" }]
    }
  }
}
```

In view JSON — activate by the **property name**:
```json
{ "$schema": "...", "dataSchema": "...", "lookups": ["branchDetail"], "view": ... }
```

In the view, read with `$lookup.branchDetail.address`, `$lookup.branchDetail.phone` — NOT an input bind target, display only.

> **filter value scope**: input/transition views use `$form.branchCode` (user is still picking); display/summary views (bound to the master schema) use `$instance.branchCode` (persisted value).

> **Antipattern**: putting `x-lookup` on `branchCode` and expecting `$lookup.branchDetail` to resolve — the name won't match (`{propertyName}` = `branchCode`, so it'd be `$lookup.branchCode`). Either name the access expression after the field, or (preferred) give the lookup its own well-named property.

### 2.5 Conditional visibility / enable

```json
{
  "spouseName": {
    "type": "string",
    "x-conditional": {
      "showIf":   { "field": "maritalStatus", "operator": "equals", "value": "married" },
      "enableIf": { "allOf": [
        { "field": "age", "operator": "greaterThan", "value": 18 },
        { "field": "consent", "operator": "equals", "value": true }
      ]}
    }
  }
}
```

Operators: `equals`, `notEquals`, `in`, `notIn`, `greaterThan`, `lessThan`, `greaterOrEqual`, `lessOrEqual`, `contains`, `notContains`, `isEmpty`, `isNotEmpty`, `matches` (regex).
Compound: `allOf`, `anyOf`, `not`.

`showIf`/`hideIf` → visibility; `enableIf`/`disableIf` → input enabled/disabled.

### 2.6 Custom async validation

```json
{
  "tcNo": {
    "type": "string",
    "pattern": "^[0-9]{11}$",
    "x-validation": {
      "rule": "tckn-checksum",
      "errorMessages": { "tr": "Geçersiz TC", "en": "Invalid TC" }
    }
  }
}
```

`delegate.onValidationRequest(field, value, formData)` is called asynchronously; the host returns an error string or `null`. It runs after schema-level pattern + length checks.

---

## 3. View authoring — node types

### 3.1 Layout

- `Column` (vertical), `Row` (horizontal), `Wrap` (overflowing rows)
- `Grid` (`columns: N`)
- `Stack` (z-axis stacking)
- `Center`, `Spacer`, `Expanded` (flex inside Row/Column)
- `ScrollView`

All carry `children: ComponentNode[]`. Spacing via `gap: 'xs' | 'sm' | 'md' | 'lg' | 'xl'`.

### 3.2 Container

- `Card` (variant: elevated/filled/outlined) — clickable via `action`/`onTap`
- `Stepper` (`steps[].content`)
- `TabView` (`tabs[].content`)
- `ExpansionPanel`
- `Dialog`, `BottomSheet`, `SideSheet` — bound to state with `visible: '$ui.xKey'`
- `Tooltip`

### 3.3 Input (each carries `bind`)

- `TextField`, `TextArea`, `SearchField`, `NumberField`
- `Dropdown`, `AutoComplete`, `RadioGroup`, `SegmentedButton`
- `Checkbox`, `Switch`
- `DatePicker`, `TimePicker`
- `Slider`

`bind`: schema property path (e.g. `firstName` or `address.city`).

### 3.4 Display

- `Text` (`content`, `variant`)
- `Icon` (`name`)
- `Image`, `Avatar`, `Badge`, `Chip`
- `ListTile` (`leading`, `title`, `subtitle`, `trailing`, `onTap`)
- `RichText`
- `Snackbar`
- `ProgressIndicator`, `LoadingIndicator`

### 3.5 Action

- `Button`, `IconButton`, `FAB`

### 3.6 Control flow

- `ForEach` (`source`, `as`, `template`) — array iteration
- `Carousel` — `source` + `template`
- `Component` — nested view by ref (via `loadComponent`)

---

## 4. Action model — view author cheat sheet

### Which node carries action on which prop?

| Node | Prop | Type | Notes |
|---|---|---|---|
| `Button`, `IconButton`, `FAB` | `action` (+ optional `command`) | `string \| ActionDescriptor` | Primary action node |
| `Card` | `action` (preferred) / `onTap` (legacy) | `ActionDescriptor \| ActionDescriptor[]` | Tappable surface |
| `ListTile` | `onTap` | `ActionDescriptor \| ActionDescriptor[]` | List row |
| `Snackbar` | `action` (inline button) | `ActionDescriptor` | Snackbar action button |
| `NavigationDrawer.items[i]` | `action` | `ActionDescriptor` | Per-item action |
| `Menu.items[i]` | `action` | `ActionDescriptor` | Per-item action |
| Form inputs (`TextField` etc.) | — | — | No action; `bind` writes to formData |

**Reserved verbs** (handled specially by the SDK):

| Verb | Behavior | Validation? | Hits host? |
|---|---|:---:|:---:|
| `'submit'` | Validate-then-dispatch | ✅ (default) | ✅ |
| `'reset'` | Clear formData, emit `'reset'` event to host | – | ✅ (runs hooks) |
| `{action:'select', bind, value}` | Inline set field/UI state | – | ❌ (skips hooks) |
| `'dispatch'` | Domain dispatch (the general case) | optional (`validate`) | ✅ |

`delegate` actions are built on `submit` / `reset`; the `command` URN customizes what runs.
`submit` validates the form by default; `dispatch` exposes an explicit `validate` flag.
**Everything else** = domain dispatch. SDK passes it through; the host (Forge) interprets.

### Pre / post hooks

`command`, `submit`, and `dispatch` actions accept `preHooks` / `postHooks` arrays — secondary
commands (audit, telemetry, …) fired around the main action:

```jsonc
{ "type": "Button", "label": "Save",
  "action": {
    "action": "dispatch",
    "command": "urn:vnext:flow:transition:onboarding:kyc-main-flow:save",
    "preHooks":  [{ "action": "audit",     "command": "urn:vnext:fn:post:onboarding:audit-click", "sync": true }],
    "postHooks": [{ "action": "telemetry", "command": "urn:vnext:fn:post:onboarding:telemetry-click" }]
  } }
```

Behavior rules:
- **Sync pre-hook reject** → main action + post-hooks skipped + error log.
- **Sync post-hook reject** → error log, remaining post-hooks still run.
- **Async hook reject** → warn log; the main action is not blocked.
- **`select`** skips hooks entirely (it never reaches the host). **`reset`** runs hooks (it does).
- **Validation fail** → no hooks fire at all.
- A **reserved verb** (`submit`/`select`/`reset`) used *as a hook* is rejected + warn log.
- The hook pipeline **never throws** — a last-ditch try/catch + error log guards it.

### 4.1 Common patterns

#### Form submit
```json
{ "type": "Button", "label": "Submit", "action": "submit", "command": "save-account" }
```

#### Workflow transition (validation required)
```json
{ "type": "Button", "label": "Continue",
  "action": "dispatch",
  "command": "urn:vnext:flow:transition:core:account-opening:next-step",
  "validate": true }
```

#### Workflow transition (no validation — e.g. back)
```json
{ "type": "Button", "label": "Back", "variant": "text",
  "action": "dispatch",
  "command": "urn:vnext:flow:transition:core:account-opening:back" }
```

#### Open dialog (UI state)
```json
{ "type": "Button", "label": "Edit",
  "action": { "action": "select", "bind": "$ui.editDialogOpen", "value": true } }
```

#### Card selection (sub-flow trigger)
```json
{ "type": "Card", "variant": "outlined",
  "action": { "action": "dispatch",
              "command": "urn:vnext:flow:transition:core:account-opening:select-deposit" },
  "children": [ { "type": "Text", "content": "Demand Deposit" } ] }
```

> Card accepts both `action` (recommended) and `onTap` (legacy alias); when both are present, `action` wins. Use `action` for new views.

#### NavigationDrawer / Menu per-item action
NavigationDrawer and Menu **do not carry a node-level action**; each item carries its own `action` field:

```json
{ "type": "NavigationDrawer",
  "visible": "$ui.drawerOpen",
  "items": [
    { "label": { "tr": "Hesaplar", "en": "Accounts" }, "icon": "account_balance",
      "action": { "action": "navigate", "command": "urn:client:nav:/accounts" } },
    { "divider": true },
    { "header": { "tr": "Ayarlar", "en": "Settings" } },
    { "label": { "tr": "Profil", "en": "Profile" }, "icon": "person",
      "action": { "action": "navigate", "command": "urn:client:nav:/profile" } }
  ]
}
```

An item is one of three kinds:
- **Tappable**: `{ label, icon?, badge?, action }` — clickable, fires action
- **Divider**: `{ divider: true }` — horizontal rule
- **Header**: `{ header: "..." }` — section header, not clickable

`Menu` uses the same pattern (`items: [{ label, icon, action }]`).

#### ListTile navigation
```json
{ "type": "ListTile", "title": "Accounts",
  "onTap": { "action": "navigate", "command": "urn:client:nav:/accounts" } }
```

#### Form reset
```json
{ "type": "Button", "label": "Clear", "variant": "text", "action": "reset" }
```

#### Multiple actions (sequential)
```json
{ "type": "Button", "label": "Save & Close",
  "action": [
    { "action": "submit" },
    { "action": "select", "bind": "$ui.dialogOpen", "value": false }
  ] }
```

### 4.2 `validate` flag — decision matrix

| Case | Verb | validate | Reason |
|---|---|:---:|---|
| Classic submit | `submit` | (default ✅) | Sending complete data to the backend |
| Save draft | `submit` | ❌ override | Incomplete form is acceptable |
| WF "Continue" | `dispatch` | ✅ override | Can't proceed with missing data |
| WF "Back" | `dispatch` | (default ❌) | Back doesn't need validation |
| Cancel | `cancel` | (default ❌) | Cancel doesn't need validation |
| Navigation | `navigate` | (default ❌) | Page change doesn't need validation |
| Reset | `reset` | (n/a) | Handled inside the SDK |

### 4.3 URN catalog

> Use `urn:vnext` for runtime commands and `urn:client` for client-local behaviors.

```
# Flow start
urn:vnext:flow:start:<domain>:<flow>

# Transition on a specific instance (${param} = instance id, bound at runtime)
urn:vnext:flow:transition:<domain>:<flow>:${param}:<transition>

# Transition on the current instance (no id segment)
urn:vnext:flow:transition:<domain>:<flow>:<transition>

# Function (cmd ∈ get|post|patch|delete; get is the default and may be omitted)
urn:vnext:fn:<cmd>:<domain>:<flow>:${param}:<fn-key>
urn:vnext:fn:<cmd>:<domain>:<fn-key>
urn:vnext:fn:<domain>:<fn-key>                        # short form, get

# Resource reference (res-key ∈ schema|flow|extension|function|view|task)
urn:vnext:res:<res-key>:<domain>:<key>                # e.g. urn:vnext:res:schema:core:input-schema

# Client-local behaviors (navigation, etc.) — free-form, interpreted by the client SDK
urn:client:nav:<route>
```

**Binding format**: `http`, `deeplink`, and `urn` values support runtime data binding via the
`${param}` placeholder (e.g. `urn:vnext:flow:transition:core:account-opening:${param}:approved`).

Group entries in the action picker by these prefixes.

---

## 5. Expression namespace cheat sheet

In view JSON, string-value fields can carry `$...` expressions:

| Prefix | Meaning | Example |
|---|---|---|
| `$form.x` | formData[x] (mutable, target of input bind) | `"content": "$form.firstName"` |
| `$instance.x` | instanceData[x] (read-only) | `"content": "$instance.customerId"` |
| `$param.x` | Param passed into a nested component | `"content": "$param.size"` |
| `$ui.x` | uiState (transient: dialog visibility, active tab) | `"visible": "$ui.dialogOpen"` |
| `$lov.x` | LOV array (loaded items) | `"content": "$lov.city"` |
| `$lookup.x.y` | Field on a lookup object | `"content": "$lookup.branchDetail.address"` |
| `$schema.x.label` | Schema field label | `"content": "$schema.firstName.label"` |
| `$item` | ForEach iteration value | `"content": "$item.name"` |
| `$context.x` | Custom delegate context | – |

### 5.1 Multi-language content

All `content`, `label`, `title`, `description` fields accept **string** or **multi-lang object**:

```json
"content": { "en": "Welcome", "tr": "Hoş geldiniz" }
```

Selected automatically based on the `lang` prop.

---

## 6. ForEach pattern

```json
{
  "type": "ForEach",
  "source": "$form.addresses",
  "as": "addr",
  "template": {
    "type": "Card",
    "children": [
      { "type": "Text", "content": "$item.label" },
      { "type": "TextField", "bind": "addresses[$index].street" }
    ]
  }
}
```

`$item` is the iteration value in template scope. `$index` is the index. The array-path convention applies inside `bind`.

In designer mode, the template renders **once** for empty sources (preview purposes).

---

## 7. Nested component pattern

```json
{
  "type": "Component",
  "ref": "address-block",
  "bind": { "value": "$form.homeAddress" }
}
```

The SDK calls `delegate.loadComponent('address-block')` and renders the returned `{ schema, view }` pair. `bind` defines parent → child data flow (the child binds its own formData to a slice of the parent's).

In the child view, read parent-provided data via `$param.value`, write local inputs with `$form.x` — these are synced back to the parent.

---

## 8. Common mistakes / antipatterns

| ❌ | ✅ | Reason |
|---|---|---|
| `"action": "transition"` (thinking it's an SDK reserved verb) | `"action": "dispatch", "command": "urn:vnext:flow:transition:...", "validate": true` | `transition` is meaningless to the SDK; dispatch with a URN |
| `"bind": "$form.firstName"` (using `$form.` prefix on input) | `"bind": "firstName"` | Input bind is a schema property path; `$form.` is only for expressions |
| `validate: true` on every button | Only on submit-like flows | Navigation/cancel/back don't need validation |
| `enum` in schema but hardcoded options in the view | `enum` + `x-enum` in schema, just `{type:'Dropdown', bind:...}` in view | Single source — i18n + validation come for free |
| Inline JSX `if/else` | `x-conditional` in schema | Logic belongs in the data layer |
| `pattern` in schema and a custom regex check in the view | Schema only | The view shouldn't know domain |
| Same action on every button | Name it semantically with verb + command | Easier host-side switch |
| Using `onTap` on Card in a new view | Use `action` | `onTap` is a legacy alias; prefer `action` |
| Single node-level action on NavigationDrawer | Per-item `action` | NavigationDrawer is item-based |

---

## 9. AI co-author rules

The order an AI co-author should follow when this guide is loaded into a skill:

1. **Create the schema first**: every field with `type`, `x-labels`, plus `enum`/`x-enum`/`x-lov`/`x-lookup`/`x-conditional`/`x-validation` as needed. Finish validation rules here.
2. **Then write the view**: layout (Column/Row/Card/...) → inputs with `bind` → action button(s) at the bottom.
3. **When picking an action**: first consider reserved (`submit`/`select`/`reset`); use domain dispatch if needed (`{action: 'dispatch', command: 'urn:vnext:flow:transition:...'}`); set `validate` to `true` only on flows that send data to the backend. Attach `preHooks`/`postHooks` for audit/telemetry side-effects (see §4).
4. **Never use the `$form` prefix in input `bind`** — only in expression contexts.
5. **Multi-lang**: every visible string is a `{en, tr, ...}` object (the sample language set comes from tenant config).
6. **Nested components use the `Component` node + the `loadComponent` delegate** — child views receive data through `$param.x`.
7. **No theme/style coercion** — never write inline style, beyond overriding the SDK's CSS variables. The component tree is defined semantically; visuals come from the theme.

### 9.1 Code review checklist

For AI-generated views, check:

- [ ] Every input's `bind` is defined in the schema?
- [ ] If schema has `enum`, the view has no hardcoded options?
- [ ] Required fields are submitted via a `Button.action === 'submit'`?
- [ ] Workflow transition buttons carry `validate: true` (where needed)?
- [ ] URN commands exist in the host's known registry?
- [ ] Multi-lang content covers all supported languages?
- [ ] `$ui.x` states are defined for any Dialog/Drawer?
- [ ] Conditional rules live in `x-conditional` (not in view JS)?

---

## 10. Quick reference — minimal form

```jsonc
// schema.json
{
  "$id": "demo:contact",
  "type": "object",
  "required": ["name", "email"],
  "properties": {
    "name":  { "type": "string", "minLength": 2, "x-labels": { "tr": "Ad", "en": "Name" } },
    "email": { "type": "string", "format": "email", "x-labels": { "tr": "E-posta", "en": "Email" } },
    "topic": {
      "type": "string",
      "enum": ["billing", "tech", "other"],
      "x-enum": {
        "billing": { "tr": "Faturalama", "en": "Billing" },
        "tech":    { "tr": "Teknik",     "en": "Technical" },
        "other":   { "tr": "Diğer",      "en": "Other" }
      }
    },
    "message": { "type": "string", "maxLength": 500, "x-labels": { "tr": "Mesaj", "en": "Message" } }
  }
}

// view.json
{
  "$schema": "https://amorphie.io/meta/view-vocabulary/1.0",
  "dataSchema": "demo:contact",
  "view": {
    "type": "Column",
    "gap": "md",
    "children": [
      { "type": "Text", "content": { "tr": "İletişim", "en": "Contact us" }, "variant": "headlineMedium" },
      { "type": "TextField", "bind": "name" },
      { "type": "TextField", "bind": "email" },
      { "type": "Dropdown",  "bind": "topic" },
      { "type": "TextArea",  "bind": "message" },
      { "type": "Row", "gap": "sm", "children": [
        { "type": "Button", "label": { "tr": "Temizle", "en": "Reset" }, "variant": "text", "action": "reset" },
        { "type": "Button", "label": { "tr": "Gönder",  "en": "Send"  }, "action": "submit", "command": "submit-contact" }
      ]}
    ]
  }
}
```

This form: 4 fields, full validation, multi-lang, reset + submit. Zero code.
