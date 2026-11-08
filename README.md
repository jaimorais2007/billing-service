# Billing Service (Oficina Mecânica - Tech Challenge Fase 4)

[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=jaimorais2007_billing-service&metric=alert_status)](https://sonarcloud.io/summary/overall?id=jaimorais2007_billing-service) [![Cobertura](https://sonarcloud.io/api/project_badges/measure?project=jaimorais2007_billing-service&metric=coverage)](https://sonarcloud.io/component_measures?id=jaimorais2007_billing-service&metric=coverage) [![CI/CD](https://github.com/jaimorais2007/billing-service/actions/workflows/ci-cd.yml/badge.svg?branch=main)](https://github.com/jaimorais2007/billing-service/actions/workflows/ci-cd.yml)

Microsserviço de **serviços, peças e pagamento** da arquitetura v2. Cobre exatamente os casos de uso abaixo, com pagamento pelo **Mercado Pago**.

Feito em **.NET 8**, com Clean Architecture, **DynamoDB** e eventos publicados no **SNS** por meio de outbox. Nenhum serviço acessa o banco de outro.

---

## Casos de uso

| Caso de uso | Rota | Sucesso | Erros |
|---|---|---|---|
| Criar serviço | `POST /api/Service` (Name, Description, DefaultPrice) | 201 (Id, Name, Description, DefaultPrice) | 400 |
| Editar serviço | `PUT /api/Service/{id}` (Name, Description, DefaultPrice) | 200 (Id, Name, Description, DefaultPrice) | 404, 400 |
| Buscar serviço por ID | `GET /api/Service/{id}` | 200 (Id, Name, Description, DefaultPrice) | 404 |
| Listar serviços | `GET /api/Service` | 200 [Id, Name, Description, DefaultPrice] | |
| Deletar serviço | `DELETE /api/Service/{id}` | 200, exclusão lógica + evento `ServiceDeleted` | 404 |
| Criar peça | `POST /api/Parts` (Name, Code, InitialQuantity, Price) | 201 (Id, Name, Code, QuantityInStock, Price) | 400 |
| Editar peça | `PUT /api/Parts/{id}` (Name, Code, Price) | 200 (Id, Name, Code, QuantityInStock, Price) | 404, 400 |
| Buscar peça por ID | `GET /api/Parts/{id}` | 200 (Id, Name, Code, QuantityInStock, Price) | 404 |
| Listar peças | `GET /api/Parts` | 200 [Id, Name, Code, QuantityInStock, Price] | |
| Deletar peça | `DELETE /api/Parts/{id}` | 204, exclusão lógica + evento `PartDeleted` | 404 |
| Adicionar estoque | `POST /api/Parts/{id}/add-stock` (Quantity) | 204 + evento `PartStockAdded` (PartId, AvailableQuantity) | 404, 400 |
| Retirar estoque | `POST /api/Parts/{id}/remove-stock` (Quantity) | 204 | 404, 400 |
| Criar pedido de pagamento | `POST /api/Payments` (ServiceOrderId, Value ou TotalAmount, ServiceIds, Parts[PartId, Quantity]) | 201 (PaymentUrl, Status, TotalAmount, TransactionId); 200 se já houver checkout aberto com o mesmo valor | 400, 409, 502 |
| Baixar estoque da OS aprovada | Evento `ServiceOrderApproved` (ServiceOrderId, Parts[PartId, Quantity]) | `PartStockEnsured` (ServiceOrderId, PartId, Quantity) por peça baixada | |
| Receber webhook do pagamento | `POST /api/webhooks/mercadopago` | 200 + `PaymentConfirmed` ou `PaymentExpired` | 404, 401 |

* Serviços e peças excluídos (inativos) não aparecem na busca nem na listagem e respondem 404.
* O código da peça é único entre as peças ativas.
* No pedido de pagamento, o valor precisa ser positivo e todos os serviços e peças informados precisam existir e estar ativos. Caso contrário, a resposta é 400 (`Invalid or inactive part or service.`). Se o Mercado Pago falhar, a resposta é 502.

### Integração Coreografada (Saga Pattern) com o OS Service

Para garantir consistência eventual entre o ciclo da OS (`os-service`) e cobrança/estoque (`billing-service`) sem acoplamento síncrono ou banco compartilhado, os serviços comunicam-se por **Saga Coreografada via SNS + SQS** (com Outbox transacional no DynamoDB):

* **Consultas síncronas de catálogo:** `GET /api/Service`, `GET /api/Parts`, `GET /api/Service/{id}` e `GET /api/Parts/{id}` (além do endpoint `POST /api/Payments` disponível via API Gateway).
* **Aceite de orçamento (`BudgetApproved`):** ao aprovar o orçamento, o `os-service` muda a OS para `WaitingPayment` e publica `BudgetApproved`. O `billing-service` consome o evento na fila `oficina-billing-events`, valida os itens e cria a preferência no Mercado Pago:
  * **Sucesso:** grava `PaymentTransaction` (`Pending`) e publica `PaymentCreated` (`serviceOrderId`, `paymentId`, `paymentUrl`, `gatewayTransactionId`).
  * **Falha (Compensação 1A):** grava o evento processado e publica `PaymentCreationFailed` (`serviceOrderId`, `reason`), fazendo o `os-service` reverter a OS para `AwaitingApproval`.
* **Recusa durante espera de pagamento (`ServiceOrderRejected`):** caso o cliente recuse a OS em `WaitingPayment`, o `billing-service` consome `ServiceOrderRejected` e cancela pagamentos `Pending` daquela OS.
* **Pagamento confirmado (`PaymentConfirmed`):** ao receber confirmação no webhook do Mercado Pago, o `billing-service` grava `GatewayPaymentId` (ID numérico do pagamento no MP), muda para `Paid` e publica `PaymentConfirmed`.

### Baixa Atômica de Estoque e Estorno Compensatório (Compensação 2)

1. Após `PaymentConfirmed`, caso a OS possua peças, o `os-service` publica `ServiceOrderApproved` (`ServiceOrderId`, `Parts[PartId, Quantity]`).
2. O `billing-service` valida **atomicamente** se todas as peças existem e possuem saldo suficiente (`QuantityInStock >= quantidade total solicitada`):
   * **Todas as peças com saldo suficiente:** debita o estoque de todas as peças e publica `PartStockEnsured` (`ServiceOrderId`, `PartId`, `Quantity`) para cada item na mesma transação DynamoDB.
   * **Qualquer peça sem estoque ou inexistente (Compensação 2):** **nenhuma peça sofre baixa parcial**; o `billing-service` solicita o estorno integral no Mercado Pago (`POST /v1/payments/{id}/refunds` com `X-Idempotency-Key`), altera o status da `PaymentTransaction` de `Paid` para `Refunded` e publica `PaymentRefunded` (`ServiceOrderId`, `GatewayTransactionId`, `Reason`), fazendo o `os-service` retroagir a OS para `AwaitingApproval` com `IsPaid = false`.
3. Todas as alterações de estado, eventos no `OUTBOX` e marcação de idempotência (`EVENT#<eventId>`) são persistidas em uma única transação DynamoDB (`TransactWriteItems`).

### Um pagamento por OS e expiração do checkout

* **Trava:** cada OS tem um item `ORDER#<osId>` com o pagamento ativo, gravado na mesma transação do pagamento com condição sobre o valor anterior. Duas requisições simultâneas para a mesma OS não criam duas cobranças: a segunda recebe 409.
* **Checkout aberto:** se a OS já tiver um checkout `Pending` ou `Refused` dentro do prazo, ele é devolvido (200) em vez de criar outro. Com valor diferente, a resposta é 409. Se a OS já estiver paga, a resposta é 400.
* **Expiração:** o checkout é criado no Mercado Pago com validade de `CheckoutExpirationMinutes` (padrão 60). A cada 5 minutos, os pagamentos `Pending` vencidos são consultados no Mercado Pago: se houver um pagamento aprovado, viram `Paid`; senão, viram `Cancelled` e é publicado `PaymentExpired` (ServiceOrderId, GatewayTransactionId).
* **Aprovação tardia:** se um checkout cancelado for aprovado depois, ele vira `Paid`, exceto quando outro pagamento da mesma OS já está pago. Nesse caso, o erro vai para o log para estorno manual.

### Webhook do Mercado Pago

1. Valida a origem pelo header `x-signature` (HMAC-SHA256 com a assinatura secreta). Sem assinatura válida: 401.
2. Notificações que não são de pagamento são ignoradas (200).
3. Consulta o pagamento no Mercado Pago pelo `data.id` e localiza a `PaymentTransaction` pelo `external_reference`. Se não encontrar: 404.
4. `approved`: status `Paid` e evento `PaymentConfirmed` (ServiceOrderId, GatewayTransactionId). O valor e a moeda precisam bater com a transação.
5. `rejected` ou `cancelled`: status `Refused` e evento `PaymentExpired` (ServiceOrderId, GatewayTransactionId).
6. Notificações repetidas não publicam o evento de novo.

---

## Banco de dados

Tabela única `oficina-billing` no DynamoDB (criada em `infra-terraform/dynamodb.tf`), com uma partição por tabela:

| Tabela | pk / sk | Colunas |
|---|---|---|
| **Service** | `SERVICE` / `<Id>` | `id`, `name`, `description`, `defaultPrice`, `inactive`, `createdAt` |
| **Part** | `PART` / `<Id>` | `id`, `name`, `code`, `quantityInStock`, `price`, `inactive`, `createdAt` |
| **PaymentTransaction** | `PAYMENT` / `<Id>` | `id`, `serviceOrderId`, `totalAmount`, `gatewayTransactionId`, `paymentUrl`, `status`, `createdAt` |

* `status`: `Pending`, `Paid`, `Refused`, `Cancelled` ou `Refunded`.
* `gatewayTransactionId` guarda o id da preferência criada no Mercado Pago e alimenta o GSI `by-gateway-transaction`.
* Cada item tem `version` para concorrência otimista. Um conflito devolve 409.
* Itens técnicos: `OUTBOX` / `<occurredAt>#<eventId>` (eventos ainda não publicados no SNS), `ORDER#<osId>` / `ACTIVE_PAYMENT` (trava de pagamento por OS) e `EVENT#<eventId>` / `PROCESSED` (mensagens já processadas, com `ttl` de 30 dias).

---

## Eventos publicados

Tópicos `arn:aws:sns:us-east-1:168126498555:oficina-<Evento>`. O envelope é o do grupo: `eventId`, `eventType`, `occurredAt`, `correlationId` e `payload`.

| Evento | Payload |
|---|---|
| `PaymentCreated` | `serviceOrderId`, `paymentId`, `paymentUrl`, `gatewayTransactionId` |
| `PaymentCreationFailed` | `serviceOrderId`, `reason` |
| `PaymentConfirmed` | `serviceOrderId`, `gatewayTransactionId` |
| `PaymentExpired` | `serviceOrderId`, `gatewayTransactionId` |
| `PaymentRefunded` | `serviceOrderId`, `gatewayTransactionId`, `reason` |
| `PartStockEnsured` | `serviceOrderId`, `partId`, `quantity` |
| `ServiceDeleted` | `serviceId` |
| `PartDeleted` | `partId` |
| `PartStockAdded` | `partId`, `availableQuantity` |

O evento é gravado na mesma transação da mudança de estado e publicado em segundo plano, sem limite de tentativas.

Eventos consumidos (fila `oficina-billing-events`): `BudgetApproved`, `ServiceOrderApproved` e `ServiceOrderRejected`, publicados pelo OS Service.

---

## Configuração

Secret `billing-service-secret` no namespace `oficina`:

| Chave | Uso |
|---|---|
| `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` | Usuário IAM `oficina-billing-service` |
| `DYNAMODB_TABLE` | `oficina-billing` |
| `SQS_QUEUE_URL` | URL da fila `oficina-billing-events` |
| `MERCADOPAGO_ACCESS_TOKEN` | Access token da aplicação no Mercado Pago |
| `MERCADOPAGO_WEBHOOK_SECRET` | Assinatura secreta, em Suas integrações > Webhooks |

Fora de `Development`, o serviço **não sobe** sem a tabela e a fila. Sem as chaves do Mercado Pago ele sobe, mas o pedido de pagamento devolve 502 e o webhook devolve 401.

No painel do Mercado Pago, configure a URL `https://kwzh4dl581.execute-api.us-east-1.amazonaws.com/api/webhooks/mercadopago` com o evento **Pagamentos**.

---

## Executar

```bash
dotnet run --project src/BillingService.Api
```

Sem AWS configurada, em `Development`, os dados ficam em memória e os eventos só aparecem no log. O Swagger fica em `http://localhost:5222/swagger`.

```bash
docker build -t billing-service .
kubectl apply -n oficina -f k8s/
```

---

## Testes

```bash
dotnet test --configuration Release --collect:"XPlat Code Coverage;Format=opencover"
```

Para validar as transações contra um DynamoDB de verdade, suba o DynamoDB Local e defina a variável abaixo. Sem ela, esse teste é ignorado.

```bash
docker run -d -p 8000:8000 amazon/dynamodb-local
DYNAMODB_LOCAL_URL=http://localhost:8000 dotnet test
```


## Observabilidade (OpenTelemetry + New Relic)

Traces, metricas e logs sao exportados via OTLP para o New Relic (`https://otlp.nr-data.net`).
Configuracao em `Configuration/OpenTelemetryConfiguration.cs`; variaveis `OTEL_*` definidas em `k8s/deployment.yaml`.
A license key vem do secret `billing-service-secret`, chave `OTEL_EXPORTER_OTLP_HEADERS` (formato `api-key=<LICENSE_KEY>`).
O exporter so e ativado quando `OTEL_EXPORTER_OTLP_ENDPOINT` esta definido.

## Qualidade e cobertura de testes

A cada PR e a cada merge na `main`, a pipeline ([`ci-templates`](https://github.com/jaimorais2007/ci-templates)) roda os testes com cobertura e:

- **falha se a cobertura de linhas ficar abaixo de 80%** (passo "Checar cobertura mínima");
- envia a análise ao **SonarCloud** e **aguarda o quality gate** (`sonar.qualitygate.wait=true`): se o gate reprovar, a pipeline falha;
- publica o relatório detalhado no resumo da execução e como artefato `coverage-report-billing-service` ([execuções da pipeline](https://github.com/jaimorais2007/billing-service/actions/workflows/ci-cd.yml)).

A `main` é protegida: só aceita PR com a verificação `pipeline / Build, testes e qualidade` aprovada.

**Evidência:** cobertura de **97,3%** no SonarCloud em 07/11/2026, com quality gate aprovado. Os selos acima mostram o valor atual. Painel: [SonarCloud — billing-service](https://sonarcloud.io/summary/overall?id=jaimorais2007_billing-service).
