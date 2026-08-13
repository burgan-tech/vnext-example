# Time Deposit Details

## Metadata

| Property | Value |
| --- | --- |
| Key | `time-deposit-input-view` |
| Domain | `core` |
| Version | 1.0.0 |
| Content Type | JSON |
| Display Mode | full-page |
| Tags | `banking`, `account-details`, `time-deposit`, `ui-view` |

## Content

```json
{
  "$schema": "https://amorphie.io/meta/view-vocabulary/1.0",
  "dataSchema": "urn:vnext:res:schema:core:time-deposit-input",
  "lookups": [
    "branchDetail"
  ],
  "view": {
    "type": "ScrollView",
    "children": [
      {
        "type": "Column",
        "gap": "md",
        "children": [
          {
            "type": "Text",
            "content": {
              "en": "Step 2 of 4 — Details",
              "tr": "Adım 2 / 4 — Bilgiler"
            },
            "variant": "labelLarge"
          },
          {
            "type": "Text",
            "content": {
              "en": "Time Deposit Details",
              "tr": "Vadeli Mevduat Bilgileri"
            },
            "variant": "headlineMedium"
          },
          {
            "type": "Text",
            "content": {
              "en": "Choose a term and how interest should be handled at maturity.",
              "tr": "Vade süresini seçin ve vade sonunda faizin nasıl işleneceğine karar verin."
            },
            "variant": "bodyMedium"
          },
          {
            "type": "Card",
            "variant": "outlined",
            "children": [
              {
                "type": "Column",
                "gap": "sm",
                "children": [
                  {
                    "type": "Text",
                    "content": {
                      "en": "Account Identity",
                      "tr": "Hesap Bilgileri"
                    },
                    "variant": "titleMedium"
                  },
                  {
                    "type": "TextField",
                    "bind": "accountName",
                    "variant": "outlined"
                  },
                  {
                    "type": "Dropdown",
                    "bind": "currency",
                    "variant": "outlined"
                  },
                  {
                    "type": "Dropdown",
                    "bind": "branchCode",
                    "variant": "outlined"
                  }
                ]
              }
            ]
          },
          {
            "type": "Card",
            "variant": "filled",
            "children": [
              {
                "type": "Column",
                "gap": "xs",
                "children": [
                  {
                    "type": "Text",
                    "content": {
                      "en": "Selected Branch",
                      "tr": "Seçilen Şube"
                    },
                    "variant": "titleSmall"
                  },
                  {
                    "type": "ListTile",
                    "title": {
                      "en": "Branch",
                      "tr": "Şube"
                    },
                    "subtitle": "$lookup.branchDetail.name",
                    "leading": {
                      "type": "Icon",
                      "name": "location_on"
                    }
                  },
                  {
                    "type": "ListTile",
                    "title": {
                      "en": "Address",
                      "tr": "Adres"
                    },
                    "subtitle": "$lookup.branchDetail.address",
                    "leading": {
                      "type": "Icon",
                      "name": "home"
                    }
                  },
                  {
                    "type": "ListTile",
                    "title": {
                      "en": "Phone",
                      "tr": "Telefon"
                    },
                    "subtitle": "$lookup.branchDetail.phone",
                    "leading": {
                      "type": "Icon",
                      "name": "smartphone"
                    }
                  },
                  {
                    "type": "ListTile",
                    "title": {
                      "en": "Branch Manager",
                      "tr": "Şube Müdürü"
                    },
                    "subtitle": "$lookup.branchDetail.manager",
                    "leading": {
                      "type": "Icon",
                      "name": "badge"
                    }
                  }
                ]
              }
            ]
          },
          {
            "type": "Card",
            "variant": "outlined",
            "children": [
              {
                "type": "Column",
                "gap": "sm",
                "children": [
                  {
                    "type": "Text",
                    "content": {
                      "en": "Term and Deposit",
                      "tr": "Vade ve Yatırım"
                    },
                    "variant": "titleMedium"
                  },
                  {
                    "type": "NumberField",
                    "bind": "initialDeposit",
                    "variant": "outlined"
                  },
                  {
                    "type": "Dropdown",
                    "bind": "termMonths",
                    "variant": "outlined"
                  },
                  {
                    "type": "Dropdown",
                    "bind": "maturityInstruction",
                    "variant": "outlined"
                  }
                ]
              }
            ]
          },
          {
            "type": "Card",
            "variant": "outlined",
            "children": [
              {
                "type": "Column",
                "gap": "sm",
                "children": [
                  {
                    "type": "Text",
                    "content": {
                      "en": "Notification Preferences",
                      "tr": "Bildirim Tercihleri"
                    },
                    "variant": "titleMedium"
                  },
                  {
                    "type": "Switch",
                    "bind": "notifications.smsNotifications"
                  },
                  {
                    "type": "Switch",
                    "bind": "notifications.emailNotifications"
                  },
                  {
                    "type": "Switch",
                    "bind": "notifications.pushNotifications"
                  }
                ]
              }
            ]
          },
          {
            "type": "Row",
            "gap": "sm",
            "mainAxisAlignment": "spaceBetween",
            "children": [
              {
                "type": "Button",
                "label": {
                  "en": "Cancel",
                  "tr": "İptal"
                },
                "variant": "outlined",
                "action": "cancel",
                "command": "urn:vnext:flow:transition:core:account-opening:cancel-account-opening"
              },
              {
                "type": "Button",
                "label": {
                  "en": "Continue",
                  "tr": "Devam Et"
                },
                "variant": "filled",
                "icon": "arrow_forward",
                "action": "submit",
                "command": "urn:vnext:flow:transition:core:account-opening:submit-time-deposit-info"
              }
            ]
          }
        ]
      }
    ]
  }
}
```


---
*Generated by vNext Forge*