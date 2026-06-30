// Next.js instrumentation hook — runs once at server startup (App Router, stable in Next 15+).
//
// Wires OpenTelemetry to the Hosty observability collector. Hosty Core injects the standard OTEL_* env
// (endpoint, http/protobuf protocol, service name, resource attributes, trace sampler) into the docker
// runtime of an app whose manifest opts into telemetry — but only when the operator has enabled
// observability and the collector is running. With no endpoint (the dev runtime, or observability off)
// we wire nothing: no localhost fallback, no export-failure noise. See
// docs/features/observability.md in the Hosty Core platform repo (not this one).
export async function register(): Promise<void> {
  // The OTLP/proto logs exporter and the console bridge are Node-only; the edge runtime gets nothing.
  if (process.env.NEXT_RUNTIME !== "nodejs") return;
  if (!process.env.OTEL_EXPORTER_OTLP_ENDPOINT) return;

  // Traces (and metrics) over OTLP. @vercel/otel reads OTEL_EXPORTER_OTLP_ENDPOINT / _PROTOCOL and the
  // OTEL_RESOURCE_ATTRIBUTES / sampler env automatically — no exporter config to keep in sync.
  const { registerOTel } = await import("@vercel/otel");
  registerOTel({ serviceName: process.env.OTEL_SERVICE_NAME ?? "media-server-web" });

  // OTLP logs: structured records (with trace_id/span_id correlation) bridged from console.* calls.
  const { setupOtlpLogs } = await import("./otel-logs");
  setupOtlpLogs();
}
