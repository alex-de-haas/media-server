import { AsyncLocalStorage } from "node:async_hooks";
import { format } from "node:util";

import { logs, SeverityNumber } from "@opentelemetry/api-logs";
import { OTLPLogExporter } from "@opentelemetry/exporter-logs-otlp-proto";
import { detectResources, envDetector } from "@opentelemetry/resources";
import { BatchLogRecordProcessor, LoggerProvider } from "@opentelemetry/sdk-logs";

type ConsoleMethod = "log" | "info" | "warn" | "error" | "debug";

let configured = false;

// Set while the exporter is shipping a batch, so any console output the exporter or the network stack
// emits during the (async) export is NOT re-ingested — prevents an export-failure → log → export
// cascade that the synchronous `emitting` flag below cannot catch.
const exportSuppression = new AsyncLocalStorage<boolean>();

// Bridge console.* into the OpenTelemetry logs signal so application logs reach the Hosty collector as
// structured OTLP records — with severity and (when emitted inside a span) trace_id/span_id
// correlation — while still printing to stdout/stderr. Hosty keeps the console (`docker logs`) and OTLP
// log streams separate and surfaces them in distinct views, so feeding both is intended, not double
// logging. The log resource is detected from the injected OTEL_RESOURCE_ATTRIBUTES (incl. hosty.app.id,
// which Core uses to attribute each record to this app). See docs/features/observability.md in the
// Hosty Core platform repo (not this one).
export function setupOtlpLogs(): void {
  if (configured) return;
  configured = true;

  // The OTLP/proto exporter reads OTEL_EXPORTER_OTLP_ENDPOINT (appends /v1/logs) from the environment.
  // Wrap export() so the whole async ship runs inside the suppression context (see exportSuppression).
  const exporter = new OTLPLogExporter();
  const originalExport = exporter.export.bind(exporter);
  exporter.export = (records, resultCallback) =>
    exportSuppression.run(true, () => originalExport(records, resultCallback));

  const provider = new LoggerProvider({
    resource: detectResources({ detectors: [envDetector] }),
    // sdk-logs 0.220 moved the exporter into the options bag and dropped the default
    // scheduledDelayMillis from 5000 to 1000. Pin it so the batch window stays a deliberate choice
    // here rather than whatever upstream's default happens to be.
    processors: [new BatchLogRecordProcessor({ exporter, scheduledDelayMillis: 5000 })],
  });
  logs.setGlobalLoggerProvider(provider);

  // Flush buffered records on container stop (SIGTERM) / Ctrl-C (SIGINT) so the last ~5s batch isn't
  // lost. These are additive `once` listeners — Next's own shutdown handling still runs.
  const shutdown = (): void => {
    void provider.shutdown().catch(() => undefined);
  };
  process.once("SIGTERM", shutdown);
  process.once("SIGINT", shutdown);

  const logger = logs.getLogger("console");
  const levels: ReadonlyArray<[ConsoleMethod, SeverityNumber, string]> = [
    ["log", SeverityNumber.INFO, "INFO"],
    ["info", SeverityNumber.INFO, "INFO"],
    ["warn", SeverityNumber.WARN, "WARN"],
    ["error", SeverityNumber.ERROR, "ERROR"],
    ["debug", SeverityNumber.DEBUG, "DEBUG"],
  ];

  const target = console as unknown as Record<ConsoleMethod, (...args: unknown[]) => void>;
  let emitting = false;
  for (const [method, severityNumber, severityText] of levels) {
    const original = target[method].bind(console);
    target[method] = (...args: unknown[]): void => {
      original(...args);
      // Skip re-ingestion: the synchronous `emitting` flag guards direct recursion, and
      // exportSuppression catches console output emitted asynchronously from within the exporter.
      if (emitting || exportSuppression.getStore()) return;
      emitting = true;
      try {
        logger.emit({ severityNumber, severityText, body: format(...args) });
      } catch {
        // Telemetry must never break the app.
      } finally {
        emitting = false;
      }
    };
  }
}
