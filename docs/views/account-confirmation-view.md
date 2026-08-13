# Confirm Account Details

## Metadata

| Property | Value |
| --- | --- |
| Key | `account-confirmation-view` |
| Domain | `core` |
| Version | 1.0.0 |
| Content Type | JSON |
| Display Mode | full-page |
| Tags | `banking`, `account-confirmation`, `approval`, `x-lookup`, `ui-view` |

## Content

```json
{
  "$schema": "https://amorphie.io/meta/view-vocabulary/1.0",
  "dataSchema": "urn:vnext:res:schema:core:account-opening-master",
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
              "en": "Step 3 of 4 — Review",
              "tr": "Adım 3 / 4 — Özet"
            },
            "variant": "labelLarge"
          },
          {
            "type": "Text",
            "content": {
              "en": "Review Your Account Details",
              "tr": "Hesap Bilgilerinizi Gözden Geçirin"
            },
            "variant": "headlineMedium"
          },
          {
            "type": "Text",
            "content": {
              "en": "Branch details below are enriched live via the get-branch-detail x-lookup on $instance.branchCode.",
              "tr": "Aşağıdaki şube bilgileri $instance.branchCode üzerinden get-branch-detail x-lookup ile canlı zenginleştirilir."
            },
            "variant": "bodyMedium"
          },
          {
            "type": "Card",
            "variant": "outlined",
            "children": [
              {
                "type": "Column",
                "gap": "xs",
                "children": [
                  {
                    "type": "Text",
                    "content": {
                      "en": "Account",
                      "tr": "Hesap"
                    },
                    "variant": "titleMedium"
                  },
                  {
                    "type": "ListTile",
                    "title": "$schema.accountType.label",
                    "subtitle": "$instance.accountType",
                    "leading": {
                      "type": "Icon",
                      "name": "home"
                    }
                  },
                  {
                    "type": "ListTile",
                    "title": "$schema.accountName.label",
                    "subtitle": "$instance.accountName",
                    "leading": {
                      "type": "Icon",
                      "name": "badge"
                    }
                  },
                  {
                    "type": "ListTile",
                    "title": "$schema.currency.label",
                    "subtitle": "$instance.currency",
                    "leading": {
                      "type": "Icon",
                      "name": "credit_card"
                    }
                  },
                  {
                    "type": "ListTile",
                    "title": "$schema.branchCode.label",
                    "subtitle": "$instance.branchCode",
                    "leading": {
                      "type": "Icon",
                      "name": "location_on"
                    }
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
                      "en": "Branch Details (lookup)",
                      "tr": "Şube Detayları (lookup)"
                    },
                    "variant": "titleMedium"
                  },
                  {
                    "type": "ListTile",
                    "title": {
                      "en": "Branch Name",
                      "tr": "Şube Adı"
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
                "gap": "xs",
                "children": [
                  {
                    "type": "Text",
                    "content": {
                      "en": "Preferences",
                      "tr": "Tercihler"
                    },
                    "variant": "titleMedium"
                  },
                  {
                    "type": "ListTile",
                    "title": "$schema.initialDeposit.label",
                    "subtitle": "$instance.initialDeposit",
                    "leading": {
                      "type": "Icon",
                      "name": "star"
                    }
                  },
                  {
                    "type": "ListTile",
                    "title": "$schema.accountPurpose.label",
                    "subtitle": "$instance.accountPurpose",
                    "leading": {
                      "type": "Icon",
                      "name": "flag"
                    }
                  },
                  {
                    "type": "ListTile",
                    "title": "$schema.notifications.label",
                    "subtitle": {
                      "en": "SMS, email, and push settings will be applied.",
                      "tr": "SMS, e-posta ve anlık bildirim ayarları uygulanacak."
                    },
                    "leading": {
                      "type": "Icon",
                      "name": "notifications"
                    }
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
                  "en": "Edit Details",
                  "tr": "Bilgileri Düzenle"
                },
                "variant": "outlined",
                "action": "back"
              },
              {
                "type": "Button",
                "label": {
                  "en": "Approve",
                  "tr": "Onayla"
                },
                "variant": "filled",
                "icon": "check",
                "action": "submit",
                "command": "urn:vnext:flow:transition:core:account-opening:approve-account-opening"
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