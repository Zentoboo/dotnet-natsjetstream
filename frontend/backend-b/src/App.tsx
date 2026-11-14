import { useCallback, useEffect, useState } from 'react'
import './App.css'

type ReplicaProduct = {
  id: string
  name: string
  price: number
  version: number
  isDeleted: boolean
  updatedAt: string
  syncedAt: string
}

type ApiResponse<T> = {
  success: boolean
  message: string | null
  data: T | null
}

const productsPath = '/api/v1/products'
const priceFormatter = new Intl.NumberFormat(undefined, {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

async function request<T>(path: string): Promise<T> {
  const response = await fetch(path)
  const text = await response.text()
  let payload: ApiResponse<T> | null = null

  if (text) {
    try {
      payload = JSON.parse(text) as ApiResponse<T>
    } catch {
      payload = null
    }
  }

  if (!response.ok || !payload?.success) {
    throw new Error(payload?.message ?? `Request failed with status ${response.status}.`)
  }

  return payload.data as T
}

function formatDate(value: string) {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

function formatTime(value: Date) {
  return value.toLocaleTimeString()
}

function App() {
  const [products, setProducts] = useState<ReplicaProduct[]>([])
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [lastLoaded, setLastLoaded] = useState<Date | null>(null)
  const [error, setError] = useState<string | null>(null)

  const loadProducts = useCallback(async (manual = false) => {
    if (manual) {
      setRefreshing(true)
    }

    try {
      const data = await request<ReplicaProduct[]>(productsPath)
      setProducts(data)
      setLastLoaded(new Date())
      setError(null)
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Unable to load the replica.')
    } finally {
      setLoading(false)
      if (manual) {
        setRefreshing(false)
      }
    }
  }, [])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      void loadProducts()
    }, 0)

    return () => window.clearTimeout(timer)
  }, [loadProducts])

  useEffect(() => {
    if (!autoRefresh) {
      return
    }

    const timer = window.setInterval(() => {
      void loadProducts()
    }, 3000)

    return () => window.clearInterval(timer)
  }, [autoRefresh, loadProducts])

  const activeCount = products.filter((product) => !product.isDeleted).length
  const deletedCount = products.length - activeCount
  const latestVersion = products.reduce((max, product) => Math.max(max, product.version), 0)

  return (
    <div className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">NATS JetStream sync lab</p>
          <h1>Backend B</h1>
          <p className="subtitle">
            Read-side replica. This view only changes after the consumer applies a committed product event.
          </p>
        </div>

        <nav className="topbar-actions" aria-label="Related services">
          <a className="ghost-link" href="http://localhost:5173">
            Backend A
          </a>
          <a className="ghost-link" href="http://127.0.0.1:4280" target="_blank" rel="noreferrer">
            Natscope
          </a>
        </nav>
      </header>

      <main className="stack">
        <section className="panel">
          <div className="section-heading">
            <div>
              <p className="eyebrow">Replica database</p>
              <h2>Synced products</h2>
            </div>

            <div className="section-meta">
              <label className="toggle">
                <input
                  type="checkbox"
                  checked={autoRefresh}
                  onChange={(event) => setAutoRefresh(event.target.checked)}
                />
                <span className="toggle-track" aria-hidden="true">
                  <span />
                </span>
                Auto refresh
              </label>
              <button className="ghost-button" type="button" onClick={() => void loadProducts(true)}>
                {refreshing ? 'Refreshing...' : 'Refresh now'}
              </button>
            </div>
          </div>

          <div className="stats">
            <div className="stat">
              <span>Total</span>
              <strong>{products.length}</strong>
            </div>
            <div className="stat">
              <span>Active</span>
              <strong>{activeCount}</strong>
            </div>
            <div className="stat">
              <span>Deleted</span>
              <strong>{deletedCount}</strong>
            </div>
            <div className="stat">
              <span>Latest version</span>
              <strong>v{latestVersion}</strong>
            </div>
            <div className="stat stat-wide">
              <span>Last checked</span>
              <strong>{lastLoaded ? formatTime(lastLoaded) : 'Waiting...'}</strong>
            </div>
          </div>

          {error && (
            <div className="alert alert-error" role="alert">
              {error}
            </div>
          )}

          {loading ? (
            <p className="empty-state">Loading replica products...</p>
          ) : products.length === 0 ? (
            <p className="empty-state">No replicated products yet. Create one in Backend A.</p>
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Product</th>
                    <th>Price</th>
                    <th>Version</th>
                    <th>Updated in A</th>
                    <th>Synced to B</th>
                    <th>Status</th>
                  </tr>
                </thead>
                <tbody>
                  {products.map((product) => (
                    <tr key={product.id} className={product.isDeleted ? 'is-deleted' : undefined}>
                      <td>
                        <strong>{product.name}</strong>
                        <span className="mono muted">{product.id}</span>
                      </td>
                      <td>{priceFormatter.format(product.price)}</td>
                      <td>
                        <span className="version-badge">v{product.version}</span>
                      </td>
                      <td>{formatDate(product.updatedAt)}</td>
                      <td>{formatDate(product.syncedAt)}</td>
                      <td>
                        <span className={product.isDeleted ? 'status deleted' : 'status active'}>
                          {product.isDeleted ? 'Deleted' : 'Active'}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      </main>

      <footer className="footer">
        <span>Backend B applies events through durable consumer b-products-replica.</span>
        <span>Changes appear here after the inbox commit and JetStream acknowledgement.</span>
      </footer>
    </div>
  )
}

export default App
