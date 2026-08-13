# vnext-example - Component Documentation

This document provides an index of all vNext components in the project.

## Additional Documents

- **[Dependency Tree](./dependency-tree.md)** — Cross-domain dependencies and inter-flow relationships
- **[OpenAPI Specs](./openapi/)** — OpenAPI 3.1 specifications for each workflow

## Summary

| Category | Count |
| --- | --- |
| Workflows | 12 |
| Tasks | 39 |
| Functions | 8 |
| Extensions | 2 |
| Schemas | 19 |
| Views | 22 |

## Workflows

| Name | Key | Description |
| --- | --- | --- |
| [Digital Banking Account Opening Workflow](./workflows/account-opening.md) | `account-opening` |  |
| [Collateral Establishment](./workflows/collateral-establishment.md) | `collateral-establishment` |  |
| [Credit Bureau Inquiry](./workflows/credit-bureau-inquiry.md) | `credit-bureau-inquiry` |  |
| [Loan Disbursement](./workflows/loan-disbursement.md) | `loan-disbursement` |  |
| [Para Transferi](./workflows/money-transfer.md) | `money-transfer` |  |
| [Payment Notification Subflow](./workflows/payment-notification-subflow.md) | `payment-notification-subflow` |  |
| [Payment Processing](./workflows/payment-process.md) | `payment-process` |  |
| [Scheduled Payments Workflow](./workflows/scheduled-payments.md) | `scheduled-payments` |  |
| [SOAP Görev Testi](./workflows/soap-task-test.md) | `soap-task-test` |  |
| [SubFlow Orchestration Child](./workflows/subflow-orchestration-child.md) | `subflow-orchestration-child` |  |
| [SubFlow Orchestration Grandchild](./workflows/subflow-orchestration-grandchild.md) | `subflow-orchestration-grandchild` |  |
| [SubFlow Orchestration Parent](./workflows/subflow-orchestration-parent.md) | `subflow-orchestration-parent` |  |

## Tasks

| Name | Key | Description |
| --- | --- | --- |
| [activate-payment-schedule](./tasks/activate-payment-schedule.md) | `activate-payment-schedule` |  |
| [archive-payment-record](./tasks/archive-payment-record.md) | `archive-payment-record` |  |
| [compute-required-approver](./tasks/compute-required-approver.md) | `compute-required-approver` |  |
| [create-bank-account](./tasks/create-bank-account.md) | `create-bank-account` |  |
| [deactivate-payment-schedule](./tasks/deactivate-payment-schedule.md) | `deactivate-payment-schedule` |  |
| [execute-transfer](./tasks/execute-transfer.md) | `execute-transfer` |  |
| [get-accounts](./tasks/get-accounts.md) | `get-accounts` |  |
| [get-branch-detail-lookup](./tasks/get-branch-detail-lookup.md) | `get-branch-detail-lookup` |  |
| [get-branches-lov](./tasks/get-branches-lov.md) | `get-branches-lov` |  |
| [get-customer-profile](./tasks/get-customer-profile.md) | `get-customer-profile` |  |
| [get-data-from-workflow](./tasks/get-data-from-workflow.md) | `get-data-from-workflow` |  |
| [get-favorite-beneficiaries-http](./tasks/get-favorite-beneficiaries-http.md) | `get-favorite-beneficiaries-http` |  |
| [get-iban-history](./tasks/get-iban-history.md) | `get-iban-history` |  |
| [get-instance](./tasks/get-instance.md) | `get-instance` |  |
| [get-instances](./tasks/get-instances.md) | `get-instances` |  |
| [get-loan-products](./tasks/get-loan-products.md) | `get-loan-products` |  |
| [get-user-info](./tasks/get-user-info.md) | `get-user-info` |  |
| [increment-retry-counter](./tasks/increment-retry-counter.md) | `increment-retry-counter` |  |
| [inquire-findeks](./tasks/inquire-findeks.md) | `inquire-findeks` |  |
| [inquire-kkb](./tasks/inquire-kkb.md) | `inquire-kkb` |  |
| [notify-application-received](./tasks/notify-application-received.md) | `notify-application-received` |  |
| [notify-approval](./tasks/notify-approval.md) | `notify-approval` |  |
| [notify-disbursed](./tasks/notify-disbursed.md) | `notify-disbursed` |  |
| [notify-rejection](./tasks/notify-rejection.md) | `notify-rejection` |  |
| [notify-state](./tasks/notify-state.md) | `notify-state` |  |
| [price-loan](./tasks/price-loan.md) | `price-loan` |  |
| [process-payment](./tasks/process-payment.md) | `process-payment` |  |
| [release-block](./tasks/release-block.md) | `release-block` |  |
| [save-payment-configuration](./tasks/save-payment-configuration.md) | `save-payment-configuration` |  |
| [score-and-limit](./tasks/score-and-limit.md) | `score-and-limit` |  |
| [send-payment-notification-sms](./tasks/send-payment-notification-sms.md) | `send-payment-notification-sms` |  |
| [send-payment-push-notification](./tasks/send-payment-push-notification.md) | `send-payment-push-notification` |  |
| [subflow-script-task](./tasks/subflow-script-task.md) | `subflow-script-task` |  |
| [transfer-to-account](./tasks/transfer-to-account.md) | `transfer-to-account` |  |
| [trigger-scheduled-payments](./tasks/trigger-scheduled-payments.md) | `trigger-scheduled-payments` |  |
| [validate-account-policies](./tasks/validate-account-policies.md) | `validate-account-policies` |  |
| [validate-application](./tasks/validate-application.md) | `validate-application` |  |
| [validate-transfer](./tasks/validate-transfer.md) | `validate-transfer` |  |
| [vip-sender](./tasks/vip-sender.md) | `vip-sender` |  |

## Functions

| Name | Key | Description |
| --- | --- | --- |
| [function-get-user-info](./functions/function-get-user-info.md) | `function-get-user-info` |  |
| [get-branch-detail](./functions/get-branch-detail.md) | `get-branch-detail` |  |
| [get-branches](./functions/get-branches.md) | `get-branches` |  |
| [Favorite Beneficiaries LOV](./functions/get-favorite-beneficiaries.md) | `get-favorite-beneficiaries` |  |
| [Source Accounts LOV](./functions/get-source-accounts.md) | `get-source-accounts` |  |
| [Loan Products LOV](./functions/loan-product-lov.md) | `loan-product-lov` |  |
| [multi-task-function-test](./functions/multi-task-function-test.md) | `multi-task-function-test` |  |
| [payment-types](./functions/payment-types.md) | `payment-types` |  |

## Extensions

| Name | Key | Description |
| --- | --- | --- |
| [Customer Profile Enrichment](./extensions/customer-profile-enrichment.md) | `customer-profile-enrichment` |  |
| [extension-user-session](./extensions/extension-user-session.md) | `extension-user-session` |  |

## Schemas

| Name | Key | Description |
| --- | --- | --- |
| [account-confirmation](./schemas/account-confirmation.md) | `account-confirmation` |  |
| [account-opening-master](./schemas/account-opening-master.md) | `account-opening-master` |  |
| [account-type-selection](./schemas/account-type-selection.md) | `account-type-selection` |  |
| [Collateral Detail](./schemas/collateral-detail.md) | `collateral-detail` |  |
| [Credit Bureau Result](./schemas/credit-bureau-result.md) | `credit-bureau-result` |  |
| [demand-deposit-input](./schemas/demand-deposit-input.md) | `demand-deposit-input` |  |
| [initiate-account-opening](./schemas/initiate-account-opening.md) | `initiate-account-opening` |  |
| [investment-account-input](./schemas/investment-account-input.md) | `investment-account-input` |  |
| [Loan Application](./schemas/loan-application.md) | `loan-application` |  |
| [Loan Approval Decision](./schemas/loan-approval-decision.md) | `loan-approval-decision` |  |
| [Loan Assessment](./schemas/loan-assessment.md) | `loan-assessment` |  |
| [Loan Disbursement Data](./schemas/loan-disbursement.md) | `loan-disbursement` |  |
| [Loan Rejection](./schemas/loan-rejection.md) | `loan-rejection` |  |
| [Money Transfer (Input)](./schemas/money-transfer-input.md) | `money-transfer-input` |  |
| [Money Transfer (Master)](./schemas/money-transfer-master.md) | `money-transfer-master` |  |
| [savings-account-input](./schemas/savings-account-input.md) | `savings-account-input` |  |
| [soap-sms-input](./schemas/soap-sms-input.md) | `soap-sms-input` |  |
| [soap-task-test-master](./schemas/soap-task-test-master.md) | `soap-task-test-master` |  |
| [time-deposit-input](./schemas/time-deposit-input.md) | `time-deposit-input` |  |

## Views

| Name | Key | Description |
| --- | --- | --- |
| [Confirm Account Details](./views/account-confirmation-view.md) | `account-confirmation-view` |  |
| [Account Opening Successful](./views/account-opening-success-view.md) | `account-opening-success-view` |  |
| [Select Account Type](./views/account-type-selection-view.md) | `account-type-selection-view` |  |
| [Loan Application Form](./views/application-intake-form.md) | `application-intake-form` |  |
| [Approval Screen](./views/approval-decision-form.md) | `approval-decision-form` |  |
| [Assessment & Pricing](./views/assessment-pricing-form.md) | `assessment-pricing-form` |  |
| [Demand Deposit Details](./views/demand-deposit-input-view.md) | `demand-deposit-input-view` |  |
| [Disbursement Completed](./views/disbursed-result.md) | `disbursed-result` |  |
| [Disbursement Summary](./views/disbursement-summary.md) | `disbursement-summary` |  |
| [Final Confirmation](./views/final-confirmation-popup-view.md) | `final-confirmation-popup-view` |  |
| [Investment Account Details](./views/investment-account-input-view.md) | `investment-account-input-view` |  |
| [Awaiting Push Approval](./views/money-transfer-awaiting-push-view.md) | `money-transfer-awaiting-push-view` |  |
| [Money Transfer Cancelled](./views/money-transfer-cancelled-view.md) | `money-transfer-cancelled-view` |  |
| [Money Transfer Receipt](./views/money-transfer-completed-view.md) | `money-transfer-completed-view` |  |
| [Money Transfer Failed](./views/money-transfer-failed-view.md) | `money-transfer-failed-view` |  |
| [Money Transfer Input Form](./views/money-transfer-input-view.md) | `money-transfer-input-view` |  |
| [Money Transfer Summary](./views/money-transfer-summary-view.md) | `money-transfer-summary-view` |  |
| [Application Rejected](./views/rejected-result.md) | `rejected-result` |  |
| [Savings Account Details](./views/savings-account-input-view.md) | `savings-account-input-view` |  |
| [SMS Details](./views/soap-sms-input-view.md) | `soap-sms-input-view` |  |
| [SMS Result](./views/soap-sms-result-view.md) | `soap-sms-result-view` |  |
| [Time Deposit Details](./views/time-deposit-input-view.md) | `time-deposit-input-view` |  |


---
*Generated by vNext Forge*