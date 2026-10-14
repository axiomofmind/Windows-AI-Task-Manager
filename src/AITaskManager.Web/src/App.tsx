import { useEffect, useMemo, useState } from 'react'
import './App.css'

type Summary = {
  observedAt: string
  collectionState: string
  gpuCount: number
  workloadCount: number
  actionsEnabled: boolean
  fanControlEnabled: boolean
}

type Diagnostics = {
  observedAt: string
  productVersion: string
  runtimeVersion: string
  paths: {
    rootDirectory: string
    configurationFile: string
    dataDirectory: string
    logDirectory: string
  }
  configuration: {
    schemaVersion: number
    createdThisRun: boolean
    migratedThisRun: boolean
    migrationBackupPath: string | null
  }
  database: {
    path: string
    schemaVersion: number
    integrity: string
    sizeBytes: number
    writeAheadLoggingEnabled: boolean
    pendingWrites: number
  }
  actions: {
    enabled: boolean
    powerControlEnabled: boolean
    fanControlEnabled: boolean
    safetyState: string
  }
  adapters: Array<{
    name: string
    state: string
    detail: string | null
  }>
  errors: string[]
}

type Metric<T> = {
  value: T | null
  state: string
  observedAt: string
  source: string
  detail: string | null
}

type Gpu = {
  id: string
  displayIndex: number
  name: string
  totalVramBytes: Metric<number>
  freeVramBytes: Metric<number>
  usedVramBytes: Metric<number>
  utilizationPercent: Metric<number>
  memoryUtilizationPercent: Metric<number>
  temperatureCelsius: Metric<number>
  powerUsageWatts: Metric<number>
  powerLimitWatts: Metric<number>
  fanSpeedPercent: Metric<number>
  graphicsClockMhz: Metric<number>
  memoryClockMhz: Metric<number>
  requestedPowerLimitWatts: Metric<number>
  defaultPowerLimitWatts: Metric<number>
  minimumPowerLimitWatts: Metric<number>
  maximumPowerLimitWatts: Metric<number>
  fanCount: Metric<number>
  slowdownTemperatureCelsius: Metric<number>
  shutdownTemperatureCelsius: Metric<number>
  minimumFanSpeedPercent: Metric<number>
  maximumFanSpeedPercent: Metric<number>
  fans: GpuFan[]
}

type GpuFan = {
  index: number
  speedPercent: Metric<number>
  targetSpeedPercent: Metric<number>
  controlPolicy: Metric<string>
}

type GpuCollection = {
  observedAt: string
  provider: string
  state: string
  driverVersion: string | null
  libraryVersion: string | null
  gpus: Gpu[]
  processes: GpuProcess[]
  error: string | null
}

type GpuProcess = {
  gpuId: string
  processId: number
  contextKind: string
  usedVramBytes: Metric<number>
}

type RuntimeEndpoint = {
  id: string
  name: string
  kind: string
  baseUrl: string
  state: string
  model: string | null
  modelFormat: string | null
  build: string | null
  modalities: string[]
  configuredContextTokens: number | null
  occupiedContextTokens: number | null
  activeSlots: number | null
  totalSlots: number | null
  queuedRequests: number | null
  promptTokensPerSecond: number | null
  generationTokensPerSecond: number | null
  promptTokensTotal: number | null
  generatedTokensTotal: number | null
  keyCacheType: string | null
  valueCacheType: string | null
  sleeping: boolean | null
  processId: number | null
  processName: string | null
  processStartedAt: string | null
  observedAt: string
  detail: string | null
}

type RuntimeCollection = {
  observedAt: string
  runtimes: RuntimeEndpoint[]
}

type GpuHistoryPoint = {
  observedAt: string
  utilizationPercent: number | null
  usedVramBytes: number | null
  temperatureCelsius: number | null
  powerUsageWatts: number | null
}

type GpuHistory = {
  observedAt: string
  windowSeconds: number
  series: Record<string, GpuHistoryPoint[]>
}

type AlertItem = {
  id: string
  severity: string
  state: string
  targetId: string
  title: string
  detail: string
  observedAt: string
}

type AlertCollection = {
  observedAt: string
  alerts: AlertItem[]
}

type ActionSession = {
  token: string
  actionsEnabled: boolean
  powerControlEnabled: boolean
  fanControlEnabled: boolean
  privilegedHelperAvailable: boolean
}

type HardwareActionResult = {
  auditId: string
  result: string
  gpuId: string
  previousWatts: number | null
  requestedWatts: number | null
  appliedWatts: number | null
  detail: string
}

type PendingPowerAction = {
  gpu: Gpu
  watts: number
  restoreDefault: boolean
  phrase: string
}

type FanControlState = {
  gpuId: string
  state: string
  requestedDutyPercent: number | null
  appliedDutyPercent: number | null
  temperatureCelsius: number | null
  leaseRemainingSeconds: number
  observedAt: string
  detail: string
}

type FanControlStateCollection = { observedAt: string; gpus: FanControlState[] }

type FanActionResult = {
  auditId: string
  result: string
  gpuId: string
  state: string
  requestedDutyPercent: number | null
  appliedDutyPercent: number | null
  temperatureCelsius: number | null
  leaseRemainingSeconds: number
  detail: string
}

type PendingFanAction = { gpu: Gpu; duty: number }

type ConnectionState = 'connecting' | 'online' | 'offline'
type ViewId = 'overview' | 'gpus' | 'workloads' | 'training' | 'models' | 'history' | 'alerts' | 'diagnostics' | 'settings'

const navigation: Array<{ id: ViewId; code: string; label: string }> = [
  { id: 'overview', code: 'OVR', label: 'Overview' },
  { id: 'gpus', code: 'GPU', label: 'GPUs' },
  { id: 'workloads', code: 'WRK', label: 'Workloads' },
  { id: 'training', code: 'TRN', label: 'Training' },
  { id: 'models', code: 'MOD', label: 'Models' },
  { id: 'history', code: 'HST', label: 'History' },
  { id: 'alerts', code: 'ALT', label: 'Alerts' },
  { id: 'diagnostics', code: 'DIA', label: 'Diagnostics' },
  { id: 'settings', code: 'CFG', label: 'Settings' },
]

const moduleDetails: Record<Exclude<ViewId, 'overview'>, { eyebrow: string; title: string; milestone: string; detail: string }> = {
  gpus: { eyebrow: 'GPU ARRAY', title: 'Device telemetry', milestone: 'M3', detail: 'Live GPU telemetry is on Overview; the dedicated device screen is staged' },
  workloads: { eyebrow: 'WORKLOAD CONTROL', title: 'Runtime processes', milestone: 'M5', detail: 'Live llama.cpp workloads are on Overview; the dedicated process screen is staged' },
  training: { eyebrow: 'TRAINING DECK', title: 'Active training runs', milestone: 'M7', detail: 'Training monitoring is not included in this alpha; GPU activity remains visible' },
  models: { eyebrow: 'MODEL BAY', title: 'Inventory and placement', milestone: 'M9', detail: 'Model inventory and placement planning are not included in this alpha' },
  history: { eyebrow: 'FLIGHT RECORDER', title: 'Telemetry history', milestone: 'M8', detail: 'History collection is active; the dedicated explorer is staged' },
  alerts: { eyebrow: 'ALERT MATRIX', title: 'Rules and incidents', milestone: 'M8', detail: 'Live alert evaluation is active; the dedicated incident screen is staged' },
  diagnostics: { eyebrow: 'DIAGNOSTICS', title: 'Control-plane state', milestone: 'M1', detail: 'Foundation diagnostics are online' },
  settings: { eyebrow: 'CONFIGURATION', title: 'System policy', milestone: 'M10', detail: 'Hardware writes remain safety locked' },
}

const formatBytes = (bytes: number | undefined) => {
  if (bytes === undefined) return '—'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function App() {
  const [summary, setSummary] = useState<Summary | null>(null)
  const [diagnostics, setDiagnostics] = useState<Diagnostics | null>(null)
  const [gpuCollection, setGpuCollection] = useState<GpuCollection | null>(null)
  const [runtimeCollection, setRuntimeCollection] = useState<RuntimeCollection | null>(null)
  const [gpuHistory, setGpuHistory] = useState<GpuHistory | null>(null)
  const [alertCollection, setAlertCollection] = useState<AlertCollection | null>(null)
  const [actionSession, setActionSession] = useState<ActionSession | null>(null)
  const [fanStates, setFanStates] = useState<FanControlStateCollection | null>(null)
  const [pendingPowerAction, setPendingPowerAction] = useState<PendingPowerAction | null>(null)
  const [pendingFanAction, setPendingFanAction] = useState<PendingFanAction | null>(null)
  const [actionBusy, setActionBusy] = useState(false)
  const [actionResult, setActionResult] = useState<HardwareActionResult | null>(null)
  const [fanActionResult, setFanActionResult] = useState<FanActionResult | null>(null)
  const [connection, setConnection] = useState<ConnectionState>('connecting')
  const [activeView, setActiveView] = useState<ViewId>('overview')

  useEffect(() => {
    const controller = new AbortController()

    const refresh = async () => {
      try {
        const [summaryResponse, diagnosticsResponse, gpuResponse, runtimeResponse, historyResponse, alertResponse, actionResponse, fanStateResponse] = await Promise.all([
          fetch('/api/v1/summary', { signal: controller.signal }),
          fetch('/api/v1/diagnostics', { signal: controller.signal }),
          fetch('/api/v1/gpus', { signal: controller.signal }),
          fetch('/api/v1/runtimes', { signal: controller.signal }),
          fetch('/api/v1/history/gpus?seconds=60', { signal: controller.signal }),
          fetch('/api/v1/alerts', { signal: controller.signal }),
          fetch('/api/v1/actions/session', { signal: controller.signal }),
          fetch('/api/v1/actions/gpu-fans/state', { signal: controller.signal }),
        ])

        if (!summaryResponse.ok || !diagnosticsResponse.ok || !gpuResponse.ok || !runtimeResponse.ok || !historyResponse.ok || !alertResponse.ok || !actionResponse.ok || !fanStateResponse.ok) {
          throw new Error('Live telemetry request failed')
        }

        const [summaryValue, diagnosticsValue, gpuValue, runtimeValue, historyValue, alertValue, actionValue, fanStateValue] = await Promise.all([
          summaryResponse.json() as Promise<Summary>,
          diagnosticsResponse.json() as Promise<Diagnostics>,
          gpuResponse.json() as Promise<GpuCollection>,
          runtimeResponse.json() as Promise<RuntimeCollection>,
          historyResponse.json() as Promise<GpuHistory>,
          alertResponse.json() as Promise<AlertCollection>,
          actionResponse.json() as Promise<ActionSession>,
          fanStateResponse.json() as Promise<FanControlStateCollection>,
        ])
        setSummary(summaryValue)
        setDiagnostics(diagnosticsValue)
        setGpuCollection(gpuValue)
        setRuntimeCollection(runtimeValue)
        setGpuHistory(historyValue)
        setAlertCollection(alertValue)
        setActionSession(actionValue)
        setFanStates(fanStateValue)
        setConnection('online')
      } catch (error: unknown) {
        if (error instanceof DOMException && error.name === 'AbortError') return
        setConnection('offline')
      }
    }

    void refresh()
    const timer = window.setInterval(() => void refresh(), 1000)
    return () => {
      window.clearInterval(timer)
      controller.abort()
    }
  }, [])

  const observedTime = useMemo(() => {
    if (!summary) return '--:--:--'
    return new Intl.DateTimeFormat(undefined, {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    }).format(new Date(summary.observedAt))
  }, [summary])

  const databaseReady = diagnostics?.database.integrity.toLowerCase() === 'ok'
  const adapterOnline = diagnostics?.adapters.filter((adapter) => adapter.state === 'online').length ?? 0
  const gpus = gpuCollection?.gpus ?? []
  const usedVram = gpus.reduce((total, gpu) => total + (gpu.usedVramBytes.value ?? 0), 0)
  const totalVram = gpus.reduce((total, gpu) => total + (gpu.totalVramBytes.value ?? 0), 0)

  const executePowerAction = async (confirmation: string) => {
    if (!pendingPowerAction || !actionSession) return
    setActionBusy(true)
    setActionResult(null)
    try {
      const response = await fetch(
        pendingPowerAction.restoreDefault
          ? '/api/v1/actions/gpu-power/restore-default'
          : '/api/v1/actions/gpu-power',
        {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'X-AITM-Action-Token': actionSession.token,
          },
          body: JSON.stringify({
            gpuId: pendingPowerAction.gpu.id,
            watts: pendingPowerAction.watts,
            confirmation,
            idempotencyKey: crypto.randomUUID(),
          }),
        },
      )
      const value = await response.json() as HardwareActionResult | { error?: string }
      if (!response.ok) throw new Error('error' in value ? value.error : 'Power action failed')
      setActionResult(value as HardwareActionResult)
      setPendingPowerAction(null)
    } catch (error: unknown) {
      setActionResult({
        auditId: 'not-created',
        result: 'failed',
        gpuId: pendingPowerAction.gpu.id,
        previousWatts: null,
        requestedWatts: pendingPowerAction.watts,
        appliedWatts: null,
        detail: error instanceof Error ? error.message : 'Power action failed',
      })
    } finally {
      setActionBusy(false)
    }
  }

  const executeFanAction = async (action: PendingFanAction, armSession: boolean) => {
    if (!actionSession || actionBusy) return
    setActionBusy(true)
    setFanActionResult(null)
    try {
      const response = await fetch('/api/v1/actions/gpu-fans/manual', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-AITM-Action-Token': actionSession.token },
        body: JSON.stringify({
          gpuId: action.gpu.id,
          dutyPercent: action.duty,
          confirmOtherControllersDisabled: armSession,
          acknowledgeManualControl: armSession,
          idempotencyKey: crypto.randomUUID(),
        }),
      })
      const value = await response.json() as FanActionResult | { error?: string }
      if (!response.ok) throw new Error('error' in value ? value.error : 'Fan action failed')
      setFanActionResult(value as FanActionResult)
      setPendingFanAction(null)
    } catch (error: unknown) {
      setFanActionResult({ auditId: 'not-created', result: 'failed', gpuId: action.gpu.id, state: armSession ? 'automatic' : 'manual', requestedDutyPercent: action.duty, appliedDutyPercent: null, temperatureCelsius: null, leaseRemainingSeconds: 0, detail: error instanceof Error ? error.message : 'Fan action failed' })
      setPendingFanAction(null)
    } finally {
      setActionBusy(false)
    }
  }

  const restoreFanAutomatic = async (gpu: Gpu) => {
    if (!actionSession || actionBusy) return
    setActionBusy(true)
    setFanActionResult(null)
    try {
      const response = await fetch('/api/v1/actions/gpu-fans/restore-automatic', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-AITM-Action-Token': actionSession.token },
        body: JSON.stringify({ gpuId: gpu.id, idempotencyKey: crypto.randomUUID() }),
      })
      const value = await response.json() as FanActionResult | { error?: string }
      if (!response.ok) throw new Error('error' in value ? value.error : 'Fan restoration failed')
      setFanActionResult(value as FanActionResult)
    } catch (error: unknown) {
      setFanActionResult({ auditId: 'not-created', result: 'failed', gpuId: gpu.id, state: 'restore-failed', requestedDutyPercent: null, appliedDutyPercent: null, temperatureCelsius: null, leaseRemainingSeconds: 0, detail: error instanceof Error ? error.message : 'Fan restoration failed' })
    } finally {
      setActionBusy(false)
    }
  }

  return (
    <div className="cockpit-shell">
      <header className="command-bar">
        <div className="brand-lockup">
          <span className="brand-mark" aria-hidden="true">
            <i />
            <i />
            <i />
          </span>
          <div>
            <span className="micro-label">LOCAL AI OPERATIONS</span>
            <h1>AI TASK MANAGER</h1>
          </div>
        </div>

        <div className="mission-clock" aria-label={`Last observation ${observedTime}`}>
          <span>OBSERVED</span>
          <strong>{observedTime}</strong>
        </div>
      </header>

      <section className="status-rail" aria-label="System status">
        <StatusCell label="Control plane" value={connection} state={connection} />
        <StatusCell
          label="Collection"
          value={summary?.collectionState ?? 'awaiting'}
          state={connection === 'online' ? 'online' : connection}
        />
        <StatusCell label="GPU array" value={`${summary?.gpuCount ?? 0} detected`} state={gpus.length > 0 ? 'online' : 'neutral'} />
        <StatusCell
          label="Database"
          value={databaseReady ? 'integrity ok' : 'checking'}
          state={databaseReady ? 'online' : 'connecting'}
        />
        <StatusCell label="VRAM" value={totalVram > 0 ? `${formatGib(usedVram)} / ${formatGib(totalVram)}` : 'awaiting'} state="neutral" />
        <StatusCell label="Alerts" value={`${alertCollection?.alerts.length ?? 0} active`} state={(alertCollection?.alerts.length ?? 0) > 0 ? 'error' : 'online'} />
        <StatusCell label="Hardware writes" value={actionSession?.powerControlEnabled ? 'power armed' : 'locked'} state={actionSession?.powerControlEnabled ? 'online' : 'locked'} />
      </section>

      <div className="workstation">
        <nav className="nav-rail" aria-label="Primary navigation">
          {navigation.map((item) => (
            <div key={item.id}>
              <NavButton
                code={item.code}
                label={item.label}
                active={activeView === item.id}
                onSelect={() => setActiveView(item.id)}
              />
            </div>
          ))}
        </nav>

        <main className="instrument-field">
          {activeView === 'overview' ? (
          <>
          <section className="primary-grid">
            <article className="instrument-panel gpu-array-panel">
              <PanelHeader eyebrow="GPU ARRAY" title="Telemetry matrix" value={`${summary?.gpuCount ?? 0} ONLINE`} />
              {gpus.length === 0 ? <div className="empty-array" role="status">
                <div className="target-reticle" aria-hidden="true">
                  <span className="reticle-ring reticle-ring--outer" />
                  <span className="reticle-ring reticle-ring--inner" />
                  <span className="reticle-cross reticle-cross--horizontal" />
                  <span className="reticle-cross reticle-cross--vertical" />
                  <span className="reticle-core" />
                </div>
                <strong>NO DEVICE FEED</strong>
                <span>{gpuCollection?.error ?? 'AWAITING NVIDIA TELEMETRY'}</span>
              </div> : <GpuCardGrid gpus={gpus} history={gpuHistory?.series} compact />}
              <div className="array-slots" aria-hidden="true">
                <span />
                <span />
                <span />
              </div>
            </article>

            <ArrayHeadroomOverview gpus={gpus} />

            <article className="instrument-panel workload-panel">
              <PanelHeader eyebrow="RUNTIME LINK" title="Inference endpoints" value={`${summary?.workloadCount ?? 0} ACTIVE`} />
              {(runtimeCollection?.runtimes.length ?? 0) > 0
                ? <RuntimeList runtimes={runtimeCollection?.runtimes ?? []} gpuCollection={gpuCollection} compact />
                : <div className="timeline" aria-label="No configured runtime endpoint">
                <div className="timeline-scale">
                  <span>-60s</span><span>-45s</span><span>-30s</span><span>-15s</span><span>NOW</span>
                </div>
                <div className="timeline-lane">
                  <span className="lane-name">INFERENCE</span>
                  <span className="lane-track"><i /></span>
                </div>
                <div className="timeline-lane">
                  <span className="lane-name">TRAINING</span>
                  <span className="lane-track"><i /></span>
                </div>
                <div className="timeline-lane">
                  <span className="lane-name">QUEUE</span>
                  <span className="lane-track"><i /></span>
                </div>
              </div>}
            </article>

            <article className="instrument-panel adapter-panel">
              <PanelHeader eyebrow="DATA LINK" title="Adapters" value={`${adapterOnline}/${diagnostics?.adapters.length ?? 4}`} />
              <div className="adapter-stack">
                {(diagnostics?.adapters ?? [
                  { name: 'nvidia', state: 'awaiting', detail: null },
                  { name: 'llama.cpp', state: 'awaiting', detail: null },
                  { name: 'lm-studio', state: 'awaiting', detail: null },
                  { name: 'training', state: 'awaiting', detail: null },
                ]).map((adapter) => (
                  <div className="adapter-row" key={adapter.name}>
                    <span className={`signal-dot signal-dot--${adapter.state}`} aria-hidden="true" />
                    <strong>{adapter.name}</strong>
                    <span>{adapter.state.replaceAll('-', ' ')}</span>
                  </div>
                ))}
              </div>
            </article>
          </section>

          </>
          ) : activeView === 'gpus' ? (
            <GpuView collection={gpuCollection} history={gpuHistory} />
          ) : activeView === 'workloads' || activeView === 'models' ? (
            <RuntimeView collection={runtimeCollection} gpuCollection={gpuCollection} />
          ) : activeView === 'history' ? (
            <HistoryView collection={gpuCollection} history={gpuHistory} />
          ) : activeView === 'alerts' ? (
            <AlertsView collection={alertCollection} />
          ) : activeView === 'diagnostics' ? (
            <DiagnosticsView diagnostics={diagnostics} connection={connection} />
          ) : activeView === 'settings' ? (
            <ControlView
              collection={gpuCollection}
              session={actionSession}
              fanStates={fanStates}
              result={actionResult}
              fanResult={fanActionResult}
              busy={actionBusy}
              onRequest={setPendingPowerAction}
              onFanRequest={(action, activeSession) => {
                if (activeSession) void executeFanAction(action, false)
                else setPendingFanAction(action)
              }}
              onFanRestore={restoreFanAutomatic}
            />
          ) : (
            <ModuleView view={activeView} diagnostics={diagnostics} connection={connection} />
          )}
        </main>
      </div>

      <footer className="footer-strip">
        <span>DESKTOP HOST // {activeView.toUpperCase()}</span>
        <span>SCHEMA {diagnostics?.database.schemaVersion ?? '—'}</span>
        <span>POWER CTRL // {actionSession?.powerControlEnabled ? 'ARMED' : 'DISABLED'}</span>
        <span>FAN CTRL // {actionSession?.fanControlEnabled ? 'WATCHDOG ARMED' : 'DISABLED'}</span>
      </footer>
      {pendingPowerAction && (
        <ActionConfirmation
          action={pendingPowerAction}
          busy={actionBusy}
          onCancel={() => setPendingPowerAction(null)}
          onConfirm={executePowerAction}
        />
      )}
      {pendingFanAction && (
        <FanConfirmation action={pendingFanAction} busy={actionBusy} onCancel={() => setPendingFanAction(null)} onConfirm={() => void executeFanAction(pendingFanAction, true)} />
      )}
    </div>
  )
}

function StatusCell({ label, value, state }: { label: string; value: string; state: string }) {
  return (
    <div className="status-cell">
      <span className={`signal-dot signal-dot--${state}`} aria-hidden="true" />
      <div><small>{label}</small><strong>{value}</strong></div>
    </div>
  )
}

function NavButton({ code, label, active = false, onSelect }: { code: string; label: string; active?: boolean; onSelect: () => void }) {
  return (
    <button
      aria-current={active ? 'page' : undefined}
      className={`nav-button${active ? ' nav-button--active' : ''}`}
      onClick={onSelect}
      type="button"
    >
      <span>{code}</span>
      <small>{label}</small>
    </button>
  )
}

function ModuleView({
  view,
  diagnostics,
  connection,
}: {
  view: Exclude<ViewId, 'overview'>
  diagnostics: Diagnostics | null
  connection: ConnectionState
}) {
  const module = moduleDetails[view]
  const isDiagnostics = view === 'diagnostics'
  const isSettings = view === 'settings'

  return (
    <section className="module-grid" aria-labelledby="module-title">
      <article className="instrument-panel module-primary">
        <PanelHeader eyebrow={module.eyebrow} title={module.title} value={isDiagnostics ? connection.toUpperCase() : module.milestone} />
        <div className="module-state">
          <div className={`module-glyph${isDiagnostics ? ' module-glyph--ready' : ''}`} aria-hidden="true">
            <span />
            <i />
          </div>
          <strong id="module-title">{isDiagnostics ? 'SYSTEM LINK ACTIVE' : 'MODULE STAGED'}</strong>
          <span>{module.detail}</span>
        </div>
      </article>

      <article className="instrument-panel module-readout-panel">
        <PanelHeader eyebrow="READOUT" title="Module status" value={module.milestone} />
        <div className="module-readouts">
          <Readout label="Control plane" value={connection} good={connection === 'online'} />
          <Readout label="Adapter" value={isDiagnostics ? `${diagnostics?.adapters.length ?? 0} registered` : 'pending'} />
          <Readout label="Actions" value={diagnostics?.actions.enabled ? 'available' : 'locked'} good={Boolean(diagnostics?.actions.enabled)} />
          <Readout label="Safety" value={diagnostics?.actions.safetyState ?? 'checking'} good={isDiagnostics} />
        </div>
        {isSettings && (
          <div className="safety-notice">
            <span aria-hidden="true">◇</span>
            {diagnostics?.actions.enabled
              ? <div><strong>GUARDED CONTROLS AVAILABLE</strong><small>Every hardware write still requires a deliberate action; manual fan control also requires a one-time safety acknowledgement and Windows approval.</small></div>
              : <div><strong>WRITE CONTROLS LOCKED</strong><small>Power and fan controls are disabled by local configuration.</small></div>}
          </div>
        )}
      </article>
    </section>
  )
}

function ArrayHeadroomOverview({ gpus }: { gpus: Gpu[] }) {
  const [mode, setMode] = useState<'thermal' | 'vram'>('thermal')
  const hottestGpu = gpus.reduce<Gpu | null>((hottest, gpu) => {
    if (gpu.temperatureCelsius.value === null) return hottest
    if (hottest?.temperatureCelsius.value === null || hottest === null) return gpu
    return gpu.temperatureCelsius.value > hottest.temperatureCelsius.value ? gpu : hottest
  }, null)
  const temperature = hottestGpu?.temperatureCelsius.value ?? null
  const slowdown = hottestGpu?.slowdownTemperatureCelsius.value ?? null
  const ceiling = slowdown === null ? null : Math.min(90, slowdown - 5)
  const headroom = temperature === null || ceiling === null ? null : Math.max(0, ceiling - temperature)
  const peakUtilization = gpus.reduce<number | null>((peak, gpu) => {
    const value = gpu.utilizationPercent.value
    return value === null ? peak : peak === null ? value : Math.max(peak, value)
  }, null)
  const usedVram = gpus.reduce((total, gpu) => total + (gpu.usedVramBytes.value ?? 0), 0)
  const totalVram = gpus.reduce((total, gpu) => total + (gpu.totalVramBytes.value ?? 0), 0)
  const vramPercent = totalVram > 0 ? (usedVram / totalVram) * 100 : null
  const freeVram = totalVram > 0 ? Math.max(0, totalVram - usedVram) : null
  const totalPower = gpus.reduce((total, gpu) => total + (gpu.powerUsageWatts.value ?? 0), 0)
  const temperatureRatio = temperature === null || ceiling === null || ceiling <= 0 ? 0 : Math.min(1, temperature / ceiling)
  const thermalState = headroom === null ? 'AWAITING' : headroom <= 3 ? 'CRITICAL' : headroom <= 10 ? 'CAUTION' : 'NOMINAL'
  const vramState = vramPercent === null ? 'AWAITING' : vramPercent >= 95 ? 'CRITICAL' : vramPercent >= 85 ? 'CAUTION' : 'NOMINAL'
  const activeState = mode === 'thermal' ? thermalState : vramState
  const activeClass = activeState === 'CRITICAL' ? 'critical' : activeState === 'CAUTION' ? 'caution' : activeState === 'NOMINAL' ? 'nominal' : 'idle'
  const ringColor = activeState === 'CRITICAL' ? 'var(--red)' : activeState === 'CAUTION' ? 'var(--amber)' : 'var(--green)'
  const ringRatio = mode === 'thermal' ? temperatureRatio : (vramPercent ?? 0) / 100

  return (
    <article className={`instrument-panel thermal-overview-panel thermal-overview-panel--${activeClass}`}>
      <PanelHeader eyebrow="ARRAY CAPACITY" title={mode === 'thermal' ? 'Thermal margin' : 'Total VRAM headroom'} value={activeState} />
      <div className="headroom-tabs" role="tablist" aria-label="Array capacity metric">
        <button aria-controls="array-headroom-readout" aria-selected={mode === 'thermal'} onClick={() => setMode('thermal')} role="tab" type="button">TEMP</button>
        <button aria-controls="array-headroom-readout" aria-selected={mode === 'vram'} onClick={() => setMode('vram')} role="tab" type="button">VRAM</button>
      </div>
      <div
        className="thermal-dial"
        id="array-headroom-readout"
        role="tabpanel"
        aria-label={mode === 'thermal'
          ? headroom === null ? 'Thermal telemetry unavailable' : `${Math.round(headroom)} degrees Celsius thermal margin`
          : freeVram === null ? 'VRAM telemetry unavailable' : `${formatGib(freeVram)} aggregate VRAM available`}
      >
        <div className="thermal-ticks" aria-hidden="true" />
        <div className="thermal-ring" style={{ background: `conic-gradient(${ringColor} ${ringRatio * 360}deg, #112838 0deg)` }} aria-hidden="true" />
        <div className="thermal-value">
          {mode === 'thermal' ? (
            <>
              <strong>{headroom === null ? '—' : Math.round(headroom)}</strong>
              <span>{headroom === null ? 'WAITING' : '°C TO CEILING'}</span>
              <small>{hottestGpu ? `GPU ${hottestGpu.displayIndex}` : 'NO GPU'} · {temperature === null ? '—' : Math.round(temperature)} / {ceiling === null ? '—' : Math.round(ceiling)} °C</small>
            </>
          ) : (
            <>
              <strong>{freeVram === null ? '—' : (freeVram / (1024 ** 3)).toFixed(1)}</strong>
              <span>{freeVram === null ? 'WAITING' : 'GB AVAILABLE'}</span>
              <small>{formatGib(usedVram)} USED · {formatGib(totalVram)} TOTAL</small>
            </>
          )}
        </div>
      </div>
      <div className={`thermal-scale thermal-scale--${mode}`}><span>{mode === 'thermal' ? 'COOL' : 'EMPTY'}</span><i /><span>{mode === 'thermal' ? 'CEILING' : 'FULL'}</span></div>
      {mode === 'thermal' ? (
        <div className="core-readouts thermal-readouts">
          <Readout label="Hottest" value={temperature === null ? '—' : `${Math.round(temperature)} °C`} good={thermalState === 'NOMINAL'} />
          <Readout label="Ceiling" value={ceiling === null ? '—' : `${Math.round(ceiling)} °C`} />
          <Readout label="Peak load" value={peakUtilization === null ? '—' : `${Math.round(peakUtilization)}%`} />
          <Readout label="Power" value={gpus.length === 0 ? '—' : `${Math.round(totalPower)} W`} />
        </div>
      ) : (
        <div className="core-readouts thermal-readouts">
          <Readout label="Available" value={freeVram === null ? '—' : formatGib(freeVram)} good={vramState === 'NOMINAL'} />
          <Readout label="Used" value={totalVram === 0 ? '—' : formatGib(usedVram)} />
          <Readout label="Capacity" value={totalVram === 0 ? '—' : formatGib(totalVram)} />
          <Readout label="Usage" value={vramPercent === null ? '—' : `${Math.round(vramPercent)}%`} />
        </div>
      )}
    </article>
  )
}

function DiagnosticsView({ diagnostics, connection }: { diagnostics: Diagnostics | null; connection: ConnectionState }) {
  const adapters = diagnostics?.adapters ?? []
  const nvidia = adapters.find(adapter => adapter.name === 'nvidia')
  const databaseReady = diagnostics?.database.integrity.toLowerCase() === 'ok'
  const errors = diagnostics?.errors ?? []
  const health = connection !== 'online'
    ? 'OFFLINE'
    : !databaseReady || nvidia?.state !== 'online' || errors.length > 0
      ? 'DEGRADED'
      : 'NOMINAL'
  const healthSignal = health === 'NOMINAL' ? 'online' : health === 'DEGRADED' ? 'connecting' : 'offline'
  const observedAt = diagnostics
    ? new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(new Date(diagnostics.observedAt))
    : '—'

  return (
    <section className="telemetry-view diagnostics-view" aria-label="Application diagnostics">
      <div className="view-titlebar">
        <div><span>DIAGNOSTICS // LIVE</span><h2>Control-plane health</h2></div>
        <div className="diagnostic-overall"><span className={`signal-dot signal-dot--${healthSignal}`} /><div><small>FOUNDATION</small><strong>{health}</strong></div></div>
      </div>

      <div className="diagnostic-health-grid">
        <DiagnosticHealth label="LOCAL API" value={connection} signal={connection} detail={`Observed ${observedAt}`} />
        <DiagnosticHealth label="DATABASE" value={databaseReady ? 'integrity ok' : diagnostics?.database.integrity ?? 'checking'} signal={databaseReady ? 'online' : 'connecting'} detail={`Schema ${diagnostics?.database.schemaVersion ?? '—'} · ${formatBytes(diagnostics?.database.sizeBytes)}`} />
        <DiagnosticHealth label="NVIDIA" value={nvidia?.state ?? 'checking'} signal={diagnosticSignal(nvidia?.state)} detail={nvidia?.detail ?? 'Collector has not reported yet'} />
        <DiagnosticHealth label="WRITE QUEUE" value={`${diagnostics?.database.pendingWrites ?? 0} pending`} signal={(diagnostics?.database.pendingWrites ?? 0) === 0 ? 'online' : 'connecting'} detail={diagnostics?.database.writeAheadLoggingEnabled ? 'WAL enabled' : 'WAL unavailable'} />
      </div>

      <div className="diagnostic-grid">
        <article className="instrument-panel diagnostic-panel diagnostic-panel--foundation">
          <PanelHeader eyebrow="FOUNDATION" title="Application and storage" value={`V${diagnostics?.configuration.schemaVersion ?? '—'}`} />
          <div className="diagnostic-readouts">
            <Readout label="App version" value={diagnostics?.productVersion ?? '—'} good />
            <Readout label=".NET runtime" value={diagnostics?.runtimeVersion ?? '—'} />
            <Readout label="Configuration" value={diagnostics?.configuration.createdThisRun ? 'created this run' : 'loaded'} good={!diagnostics?.configuration.createdThisRun} />
            <Readout label="Migration" value={diagnostics?.configuration.migratedThisRun ? 'migrated this run' : 'not required'} good={!diagnostics?.configuration.migratedThisRun} />
            <Readout label="Database mode" value={diagnostics?.database.writeAheadLoggingEnabled ? 'WAL' : 'standard'} good={diagnostics?.database.writeAheadLoggingEnabled} />
            <Readout label="Database size" value={formatBytes(diagnostics?.database.sizeBytes)} />
          </div>
        </article>

        <article className="instrument-panel diagnostic-panel diagnostic-panel--actions">
          <PanelHeader eyebrow="SAFETY ENVELOPE" title="Hardware write gates" value={diagnostics?.actions.enabled ? 'ENABLED' : 'LOCKED'} />
          <div className="gate-stack">
            <DiagnosticGate label="Global actions" enabled={Boolean(diagnostics?.actions.enabled)} />
            <DiagnosticGate label="Power control" enabled={Boolean(diagnostics?.actions.powerControlEnabled)} />
            <DiagnosticGate label="Fan control" enabled={Boolean(diagnostics?.actions.fanControlEnabled)} />
          </div>
          <div className="safety-mode"><small>ACTIVE POLICY</small><strong>{diagnostics?.actions.safetyState.replaceAll('-', ' ') ?? 'CHECKING'}</strong></div>
        </article>

        <article className="instrument-panel diagnostic-panel diagnostic-panel--adapters">
          <PanelHeader eyebrow="DATA LINKS" title="Adapter status" value={`${adapters.filter(adapter => adapter.state === 'online').length}/${adapters.length || 4} ONLINE`} />
          <div className="diagnostic-adapters">
            {adapters.length > 0 ? adapters.map(adapter => (
              <div className="diagnostic-adapter" key={adapter.name}>
                <span className={`signal-dot signal-dot--${diagnosticSignal(adapter.state)}`} aria-hidden="true" />
                <div><strong>{adapter.name}</strong><small>{adapter.detail ?? 'No additional detail reported.'}</small></div>
                <em>{adapter.state.replaceAll('-', ' ')}</em>
              </div>
            )) : <div className="diagnostic-empty">WAITING FOR ADAPTER DIAGNOSTICS</div>}
          </div>
        </article>

        <article className="instrument-panel diagnostic-panel diagnostic-panel--paths">
          <PanelHeader eyebrow="LOCAL STORAGE" title="Application paths" value="LOCAL ONLY" />
          <div className="path-stack">
            <DiagnosticPath label="Configuration" value={diagnostics?.paths.configurationFile} />
            <DiagnosticPath label="Database" value={diagnostics?.database.path} />
            <DiagnosticPath label="Logs" value={diagnostics?.paths.logDirectory} />
            <DiagnosticPath label="Data" value={diagnostics?.paths.dataDirectory} />
          </div>
        </article>
      </div>

      {errors.length > 0 && (
        <article className="instrument-panel diagnostic-errors">
          <PanelHeader eyebrow="FAULT LOG" title="Active diagnostic errors" value={`${errors.length} ACTIVE`} />
          {errors.map((error, index) => <div key={`${index}-${error}`}><span className="signal-dot signal-dot--error" /><strong>{error}</strong></div>)}
        </article>
      )}
    </section>
  )
}

function DiagnosticHealth({ label, value, signal, detail }: { label: string; value: string; signal: string; detail: string }) {
  return (
    <article className="diagnostic-health">
      <span className={`signal-dot signal-dot--${signal}`} aria-hidden="true" />
      <div><small>{label}</small><strong>{value}</strong><p>{detail}</p></div>
    </article>
  )
}

function DiagnosticGate({ label, enabled }: { label: string; enabled: boolean }) {
  return <div className="diagnostic-gate"><span>{label}</span><i className={enabled ? 'gate-enabled' : ''} /><strong>{enabled ? 'ENABLED' : 'LOCKED'}</strong></div>
}

function DiagnosticPath({ label, value }: { label: string; value?: string }) {
  return <div className="diagnostic-path"><small>{label}</small><strong title={value}>{value ?? '—'}</strong></div>
}

function diagnosticSignal(state?: string): string {
  if (state === 'online' || state === 'ready' || state === 'active') return 'online'
  if (state === 'loading' || state === 'awaiting') return 'connecting'
  if (state === 'offline' || state === 'error') return 'offline'
  return 'neutral'
}

function ControlView({
  collection,
  session,
  fanStates,
  result,
  fanResult,
  busy,
  onRequest,
  onFanRequest,
  onFanRestore,
}: {
  collection: GpuCollection | null
  session: ActionSession | null
  fanStates: FanControlStateCollection | null
  result: HardwareActionResult | null
  fanResult: FanActionResult | null
  busy: boolean
  onRequest: (action: PendingPowerAction) => void
  onFanRequest: (action: PendingFanAction, activeSession: boolean) => void
  onFanRestore: (gpu: Gpu) => void
}) {
  const gpus = collection?.gpus ?? []
  const [selectedPower, setSelectedPower] = useState<Record<string, number>>({})
  const [selectedFan, setSelectedFan] = useState<Record<string, number>>({})
  const powerEnabled = Boolean(session?.actionsEnabled && session.powerControlEnabled && session.privilegedHelperAvailable)
  const fanEnabled = Boolean(session?.actionsEnabled && session.fanControlEnabled && session.privilegedHelperAvailable)
  const restoreAvailable = Boolean(session?.privilegedHelperAvailable)

  return (
    <section className="telemetry-view" aria-label="GPU hardware controls">
      <div className="view-titlebar">
        <div><span>CONTROL BUS // GUARDED · FAN EXPERIMENTAL</span><h2>Power and cooling policy</h2></div>
        <strong>{fanEnabled ? 'FAN WATCHDOG ARMED' : powerEnabled ? 'POWER ARMED' : 'WRITES LOCKED'}</strong>
      </div>
      {result && <div className={`action-result action-result--${result.result}`}><strong>{result.result}</strong><span>{result.detail}</span><small>AUDIT {result.auditId}</small></div>}
      {fanResult && <div className={`action-result action-result--${fanResult.result}`}><strong>{fanResult.state}</strong><span>{fanResult.detail}</span><small>AUDIT {fanResult.auditId}</small></div>}
      <div className="control-grid">
        {gpus.map((gpu) => {
          const minimum = gpu.minimumPowerLimitWatts.value
          const maximum = gpu.maximumPowerLimitWatts.value
          const current = gpu.requestedPowerLimitWatts.value ?? gpu.powerLimitWatts.value
          const factory = gpu.defaultPowerLimitWatts.value
          const selected = selectedPower[gpu.id] ?? current ?? minimum ?? 0
          const suffix = gpu.id.slice(-8).toUpperCase()
          const powerReady = powerEnabled && minimum !== null && maximum !== null && factory !== null
          const fanMinimum = gpu.minimumFanSpeedPercent.value
          const fanMaximum = gpu.maximumFanSpeedPercent.value
          const activeFan = fanStates?.gpus.find(state => state.gpuId.toLowerCase() === gpu.id.toLowerCase())
          const telemetryManual = gpu.fans.some(fan => fan.controlPolicy.value?.toLowerCase() === 'manual')
          const selectedDuty = selectedFan[gpu.id] ?? activeFan?.requestedDutyPercent ?? gpu.fanSpeedPercent.value ?? fanMinimum ?? 0
          const fanReady = fanEnabled && fanMinimum !== null && fanMaximum !== null && gpu.slowdownTemperatureCelsius.value !== null
          const emergencyCeiling = gpu.slowdownTemperatureCelsius.value === null ? null : Math.min(90, gpu.slowdownTemperatureCelsius.value - 5)
          return (
            <article className="instrument-panel control-card" key={gpu.id}>
              <PanelHeader eyebrow={`GPU ${gpu.displayIndex} // ${suffix}`} title={shortGpuName(gpu.name)} value={`${current ?? '—'} W`} />
              <div className="power-envelope">
                <div className="power-scale"><span>{minimum ?? '—'} W</span><strong>{selected} W</strong><span>{maximum ?? '—'} W</span></div>
                <input
                  aria-label={`GPU ${gpu.displayIndex} power limit`}
                  disabled={!powerReady}
                  max={maximum ?? 1}
                  min={minimum ?? 0}
                  onChange={(event) => setSelectedPower(values => ({ ...values, [gpu.id]: Number(event.target.value) }))}
                  step="1"
                  type="range"
                  value={selected}
                />
                <div className="control-actions">
                  <button disabled={busy || !powerReady || selected === current} onClick={() => onRequest({ gpu, watts: selected, restoreDefault: false, phrase: `SET ${suffix} ${selected}W` })} type="button">APPLY {selected} W</button>
                  <button disabled={busy || !powerReady || factory === current} onClick={() => onRequest({ gpu, watts: factory ?? 0, restoreDefault: true, phrase: `RESTORE ${suffix} DEFAULT` })} type="button">RESTORE {factory ?? '—'} W</button>
                </div>
              </div>
              <div className="fan-capability">
                <div><small>GPU FAN CHANNELS</small><strong>{gpu.fanCount.value ?? 'UNAVAILABLE'}</strong></div>
                <div><small>POLICY</small><strong>{gpu.fans.map(fan => fan.controlPolicy.value ?? fan.controlPolicy.state).join(' / ') || 'UNAVAILABLE'}</strong></div>
                <div><small>SUPPORTED DUTY</small><strong>{gpu.minimumFanSpeedPercent.value ?? '—'}–{gpu.maximumFanSpeedPercent.value ?? '—'}%</strong></div>
                <div><small>WATCHDOG STATE</small><strong className={activeFan ? 'active-value' : ''}>{activeFan?.state ?? (telemetryManual ? 'MANUAL / UNOWNED' : 'AUTOMATIC')}</strong></div>
              </div>
              <div className="fan-envelope">
                <div className="fan-scale"><span>{fanMinimum ?? '—'}%</span><strong>{Math.round(selectedDuty)}%</strong><span>{fanMaximum ?? '—'}%</span></div>
                <input aria-label={`GPU ${gpu.displayIndex} manual fan minimum`} disabled={!fanReady} max={fanMaximum ?? 100} min={fanMinimum ?? 0} onChange={(event) => setSelectedFan(values => ({ ...values, [gpu.id]: Number(event.target.value) }))} step="1" type="range" value={selectedDuty} />
                <div className="fan-safety-strip">
                  <span><small>THERMAL CEILING</small><strong>{emergencyCeiling ?? '—'} °C</strong></span>
                  <span><small>APPLIED / LEASE</small><strong>{activeFan?.appliedDutyPercent ?? '—'}% / {activeFan?.leaseRemainingSeconds ?? 0}s</strong></span>
                </div>
                <div className="control-actions fan-actions">
                  <button disabled={busy || !fanReady || (Boolean(activeFan) && activeFan?.requestedDutyPercent === Math.round(selectedDuty))} onClick={() => onFanRequest({ gpu, duty: Math.round(selectedDuty) }, Boolean(activeFan))} type="button">{activeFan ? 'APPLY' : 'ENABLE'} {Math.round(selectedDuty)}%</button>
                  <button disabled={busy || !restoreAvailable || (!activeFan && !telemetryManual)} onClick={() => onFanRestore(gpu)} type="button">RESTORE NVIDIA AUTO</button>
                </div>
              </div>
            </article>
          )
        })}
      </div>
      <div className="safety-notice control-safety">
        <span aria-hidden="true">◇</span>
        <div><strong>MANUAL DUTY IS A MINIMUM, NOT A FIXED SPEED</strong><small>The elevated watchdog raises duty as temperature approaches the device-derived ceiling and restores NVIDIA automatic control if the app, IPC link, heartbeat, or telemetry fails. One NVML channel may represent multiple physical fans.</small></div>
      </div>
    </section>
  )
}

function FanConfirmation({
  action,
  busy,
  onCancel,
  onConfirm,
}: {
  action: PendingFanAction
  busy: boolean
  onCancel: () => void
  onConfirm: () => void
}) {
  const [controllersDisabled, setControllersDisabled] = useState(false)
  const slowdown = action.gpu.slowdownTemperatureCelsius.value
  const ceiling = slowdown === null ? '—' : Math.min(90, slowdown - 5)
  return (
    <div className="confirmation-backdrop" role="presentation">
      <section aria-labelledby="fan-confirmation-title" aria-modal="true" className="confirmation-dialog" role="dialog">
        <span>MANUAL FAN SESSION // ONE-TIME APPROVAL</span>
        <h2 id="fan-confirmation-title">Use {action.duty}% as the minimum?</h2>
        <p className="confirmation-device">{shortGpuName(action.gpu.name)}</p>
        <div className="confirmation-instruments">
          <Readout label="GPU" value={`${action.gpu.displayIndex} // ${action.gpu.id.slice(-8).toUpperCase()}`} />
          <Readout label="Temperature" value={`${action.gpu.temperatureCelsius.value ?? '—'} °C`} />
          <Readout label="Channels" value={`${action.gpu.fanCount.value ?? '—'}`} />
          <Readout label="Policy" value={action.gpu.fans.map(fan => fan.controlPolicy.value ?? fan.controlPolicy.state).join(' / ') || '—'} />
        </div>
        <label className="controller-confirm"><input autoFocus checked={controllersDisabled} disabled={busy} onChange={(event) => setControllersDisabled(event.target.checked)} type="checkbox" /><span>I have closed Fan Control, Afterburner, Precision X, and any other application that changes these GPU fans.</span></label>
        <p>This approval starts one guarded manual session. You can adjust the minimum afterward without retyping or approving UAC again. The watchdog raises fan speed near the {ceiling} °C ceiling and restores NVIDIA automatic control after disable, heartbeat loss, or app exit.</p>
        <div className="confirmation-actions">
          <button disabled={busy} onClick={onCancel} type="button">CANCEL</button>
          <button disabled={busy || !controllersDisabled} onClick={onConfirm} type="button">{busy ? 'AWAITING UAC…' : 'START MANUAL SESSION'}</button>
        </div>
      </section>
    </div>
  )
}

function ActionConfirmation({
  action,
  busy,
  onCancel,
  onConfirm,
}: {
  action: PendingPowerAction
  busy: boolean
  onCancel: () => void
  onConfirm: (confirmation: string) => void
}) {
  const [confirmation, setConfirmation] = useState('')
  return (
    <div className="confirmation-backdrop" role="presentation">
      <section aria-labelledby="confirmation-title" aria-modal="true" className="confirmation-dialog" role="dialog">
        <span>HARDWARE ACTION // ADMINISTRATOR</span>
        <h2 id="confirmation-title">{action.restoreDefault ? 'Restore factory power limit' : `Set ${action.watts} W power limit`}</h2>
        <div className="confirmation-instruments">
          <Readout label="GPU" value={`${action.gpu.displayIndex} // ${action.gpu.id.slice(-8).toUpperCase()}`} />
          <Readout label="Temperature" value={`${action.gpu.temperatureCelsius.value ?? '—'} °C`} />
          <Readout label="Current" value={`${action.gpu.requestedPowerLimitWatts.value ?? action.gpu.powerLimitWatts.value ?? '—'} W`} />
          <Readout label="Requested" value={`${action.watts} W`} />
        </div>
        <label>TYPE TO CONFIRM <strong>{action.phrase}</strong><input autoFocus disabled={busy} onChange={(event) => setConfirmation(event.target.value)} value={confirmation} /></label>
        <p>Windows will request administrator approval. The helper accepts only this UUID-bound power operation and NVML will be read back afterward.</p>
        <div className="confirmation-actions">
          <button disabled={busy} onClick={onCancel} type="button">CANCEL</button>
          <button disabled={busy || confirmation !== action.phrase} onClick={() => void onConfirm(confirmation)} type="button">{busy ? 'AWAITING UAC…' : 'AUTHORIZE'}</button>
        </div>
      </section>
    </div>
  )
}

function PanelHeader({ eyebrow, title, value }: { eyebrow: string; title: string; value: string }) {
  return (
    <header className="panel-header">
      <div><span>{eyebrow}</span><h2>{title}</h2></div>
      <strong>{value}</strong>
    </header>
  )
}

function Readout({ label, value, good = false }: { label: string; value: string; good?: boolean }) {
  return (
    <div className={`core-readout${good ? ' core-readout--good' : ''}`}>
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  )
}

function GpuView({ collection, history }: { collection: GpuCollection | null; history: GpuHistory | null }) {
  const gpus = collection?.gpus ?? []
  return (
    <section className="telemetry-view" aria-label="NVIDIA GPU telemetry">
      <div className="view-titlebar">
        <div><span>GPU ARRAY // LIVE</span><h2>NVIDIA telemetry</h2></div>
        <div className="driver-readout"><small>DRIVER</small><strong>{collection?.driverVersion ?? '—'}</strong><small>NVML {collection?.libraryVersion ?? '—'}</small></div>
      </div>
      {gpus.length > 0
        ? <GpuCardGrid gpus={gpus} history={history?.series} />
        : <div className="instrument-panel empty-view"><strong>NO GPU TELEMETRY</strong><span>{collection?.error ?? 'Collector is starting'}</span></div>}
    </section>
  )
}

function GpuCardGrid({
  gpus,
  history,
  compact = false,
}: {
  gpus: Gpu[]
  history?: Record<string, GpuHistoryPoint[]>
  compact?: boolean
}) {
  return (
    <div className={`gpu-card-grid${compact ? ' gpu-card-grid--compact' : ''}`}>
      {gpus.map((gpu) => {
        const total = gpu.totalVramBytes.value
        const used = gpu.usedVramBytes.value
        const vramPercent = total && used !== null ? (used / total) * 100 : null
        return (
          <article className="gpu-card" key={gpu.id}>
            <header><span>GPU {gpu.displayIndex}</span><strong>{shortGpuName(gpu.name)}</strong><small>{gpu.id.slice(-8).toUpperCase()}</small></header>
            <div className="gpu-primary">
              <MetricDial label="CORE" value={gpu.utilizationPercent.value} unit="%" />
              <MetricDial label="TEMP" value={gpu.temperatureCelsius.value} unit="°C" />
              <MetricDial label="POWER" value={gpu.powerUsageWatts.value} unit="W" />
              <MetricDial label="FAN" value={gpu.fanSpeedPercent.value} unit="%" />
            </div>
            <CapacityBar label="VRAM" value={vramPercent} text={used !== null && total !== null ? `${formatGib(used)} / ${formatGib(total)}` : metricState(gpu.usedVramBytes)} />
            <Sparkline points={(history?.[gpu.id] ?? []).map(point => point.utilizationPercent)} label="60 second GPU utilization" />
            <div className="gpu-secondary">
              <span><small>MEM LOAD</small><strong>{formatMetric(gpu.memoryUtilizationPercent, '%')}</strong></span>
              <span><small>CORE CLK</small><strong>{formatMetric(gpu.graphicsClockMhz, ' MHz')}</strong></span>
              <span><small>MEM CLK</small><strong>{formatMetric(gpu.memoryClockMhz, ' MHz')}</strong></span>
              <span><small>LIMIT</small><strong>{formatMetric(gpu.powerLimitWatts, ' W')}</strong></span>
            </div>
          </article>
        )
      })}
    </div>
  )
}

function HistoryView({ collection, history }: { collection: GpuCollection | null; history: GpuHistory | null }) {
  const gpus = collection?.gpus ?? []
  return (
    <section className="telemetry-view" aria-label="GPU history">
      <div className="view-titlebar"><div><span>FLIGHT RECORDER // ROLLING</span><h2>60-second GPU history</h2></div><strong>{history?.windowSeconds ?? 60}s WINDOW</strong></div>
      {gpus.length > 0
        ? <GpuCardGrid gpus={gpus} history={history?.series} />
        : <div className="instrument-panel empty-view"><strong>NO HISTORY YET</strong><span>History begins when GPU telemetry is online.</span></div>}
    </section>
  )
}

function AlertsView({ collection }: { collection: AlertCollection | null }) {
  const alerts = collection?.alerts ?? []
  return (
    <section className="telemetry-view" aria-label="Active alerts">
      <div className="view-titlebar"><div><span>ALERT MATRIX // LIVE</span><h2>Active conditions</h2></div><strong>{alerts.length} ACTIVE</strong></div>
      <div className="instrument-panel alert-panel">
        {alerts.length === 0
          ? <div className="empty-view empty-view--good"><strong>ALL SYSTEMS NOMINAL</strong><span>No active thermal, memory, telemetry, or runtime alerts.</span></div>
          : <div className="alert-list">{alerts.map(alert => (
              <article className={`alert-card alert-card--${alert.severity}`} key={alert.id}>
                <span className={`signal-dot signal-dot--${alert.severity === 'error' ? 'error' : 'connecting'}`} />
                <div><small>{alert.severity} // {alert.targetId}</small><strong>{alert.title}</strong><p>{alert.detail}</p></div>
              </article>
            ))}</div>}
      </div>
    </section>
  )
}

function Sparkline({ points, label }: { points: Array<number | null>; label: string }) {
  const usable = points.filter((value): value is number => value !== null)
  if (usable.length < 2) return <div className="sparkline sparkline--empty" aria-label={`${label}: collecting`} />
  const coordinates = points.map((value, index) => {
    const x = points.length === 1 ? 0 : (index / (points.length - 1)) * 100
    const y = 30 - ((value ?? 0) / 100) * 28
    return `${x.toFixed(2)},${Math.max(1, Math.min(30, y)).toFixed(2)}`
  }).join(' ')
  return <svg className="sparkline" viewBox="0 0 100 32" preserveAspectRatio="none" role="img" aria-label={label}><polyline points={coordinates} /></svg>
}

function RuntimeView({ collection, gpuCollection }: { collection: RuntimeCollection | null; gpuCollection: GpuCollection | null }) {
  const runtimes = collection?.runtimes ?? []
  return (
    <section className="telemetry-view" aria-label="Inference runtimes">
      <div className="view-titlebar"><div><span>RUNTIME LINK // LIVE</span><h2>Inference workloads</h2></div><strong>{runtimes.length} CONFIGURED</strong></div>
      <div className="instrument-panel runtime-panel">
        {runtimes.length > 0
          ? <RuntimeList runtimes={runtimes} gpuCollection={gpuCollection} />
          : <div className="empty-view"><strong>NO LLAMA.CPP SERVERS</strong><span>Start a local llama-server or add an explicit endpoint in configuration.</span></div>}
      </div>
    </section>
  )
}

function RuntimeList({
  runtimes,
  gpuCollection,
  compact = false,
}: {
  runtimes: RuntimeEndpoint[]
  gpuCollection: GpuCollection | null
  compact?: boolean
}) {
  return (
    <div className={`runtime-list${compact ? ' runtime-list--compact' : ''}`}>
      {runtimes.map((runtime) => {
        const placements = gpuPlacements(runtime.processId, gpuCollection)
        return (
        <article className="runtime-card" key={runtime.id}>
          <span className={`signal-dot signal-dot--${runtimeStateSignal(runtime.state)}`} aria-hidden="true" />
          <div className="runtime-identity">
            <small>{runtime.kind}{runtime.processId ? ` // PID ${runtime.processId}` : ''}</small>
            <strong>{runtime.name}</strong>
            <span>{runtime.model ?? runtime.baseUrl}{runtime.modelFormat ? ` · ${runtime.modelFormat}` : ''}{runtime.modalities.length > 0 ? ` · ${runtime.modalities.join('/')}` : ''}</span>
            {!compact && <em>GPU {placements || 'UNATTRIBUTED'} · KV {runtime.keyCacheType ?? '—'} / {runtime.valueCacheType ?? '—'} · BUILD {runtime.build ?? '—'}</em>}
          </div>
          <div className="runtime-stat"><small>STATE / QUEUE</small><strong>{runtime.state} / {runtime.queuedRequests ?? '—'}</strong></div>
          <div className="runtime-stat"><small>CONTEXT</small><strong>{formatContext(runtime.occupiedContextTokens, runtime.configuredContextTokens)}</strong></div>
          <div className="runtime-stat"><small>PROMPT</small><strong>{formatRate(runtime.promptTokensPerSecond)}</strong></div>
          <div className="runtime-stat"><small>GENERATE</small><strong>{formatRate(runtime.generationTokensPerSecond)}</strong></div>
          <div className="runtime-stat"><small>SLOTS</small><strong>{runtime.activeSlots ?? '—'} / {runtime.totalSlots ?? '—'}</strong></div>
        </article>
        )
      })}
    </div>
  )
}

function MetricDial({ label, value, unit }: { label: string; value: number | null; unit: string }) {
  return <div className="metric-dial"><small>{label}</small><strong>{value === null ? '—' : Math.round(value)}</strong><span>{value === null ? '' : unit}</span></div>
}

function CapacityBar({ label, value, text }: { label: string; value: number | null; text: string }) {
  const width = value === null ? 0 : Math.min(100, Math.max(0, value))
  return <div className="capacity"><div><span>{label}</span><strong>{text}</strong></div><div className="capacity-track"><i style={{ width: `${width}%` }} /></div></div>
}

const formatGib = (bytes: number) => `${(bytes / (1024 ** 3)).toFixed(1)} GB`
const metricState = <T,>(metric: Metric<T>) => metric.state.replaceAll('-', ' ').toUpperCase()
const formatMetric = (metric: Metric<number>, unit: string) => metric.value === null ? metricState(metric) : `${Math.round(metric.value)}${unit}`
const shortGpuName = (name: string) => name.replace('NVIDIA ', '').replace(' Workstation Edition', '')
const runtimeStateSignal = (state: string) => ['ready', 'active', 'idle'].includes(state) ? 'online' : state === 'loading' ? 'connecting' : 'offline'
const formatRate = (value: number | null) => value === null ? '—' : `${value.toFixed(1)} tok/s`
const formatContext = (occupied: number | null, capacity: number | null) => `${occupied?.toLocaleString() ?? '—'} / ${capacity?.toLocaleString() ?? '—'}`
const gpuPlacements = (processId: number | null, collection: GpuCollection | null) => {
  if (processId === null || collection === null) return ''
  const indexes = collection.processes
    .filter(process => process.processId === processId)
    .map(process => collection.gpus.find(gpu => gpu.id === process.gpuId)?.displayIndex)
    .filter((index): index is number => index !== undefined)
  return [...new Set(indexes)].sort((left, right) => left - right).join(',')
}

export default App
