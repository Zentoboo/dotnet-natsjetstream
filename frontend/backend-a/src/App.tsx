import { useCallback, useEffect, useState, type FormEvent } from 'react'
import './App.css'

type Product = {
  id: string
  name: string
  price: number
  version: number
  isDeleted: boolean
  updatedAt: string
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

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: init?.body ? { 'Content-Type': 'application/json' } : undefined,
  })

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

function App() {
  const [products, setProducts] = useState<Product[]>([])
  const [name, setName] = useState('')
  const [price, setPrice] = useState('')
  const [editingId, setEditingId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const loadProducts = useCallback(async (showSpinner = true) => {
    if (showSpinner) {
      setLoading(true)
    }

    try {
      const data = await request<Product[]>(productsPath)
      setProducts(data)
      setError(null)
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Unable to load products.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      void loadProducts()
    }, 0)

    return () => window.clearTimeout(timer)
  }, [loadProducts])

  function resetForm() {
    setName('')
    setPrice('')
    setEditingId(null)
  }

  function startEdit(product: Product) {
    setEditingId(product.id)
    setName(product.name)
    setPrice(String(product.price))
    setError(null)
    setNotice(null)
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    const parsedPrice = Number(price)
    if (!name.trim() || !Number.isFinite(parsedPrice) || parsedPrice < 0) {
      setError('Enter a product name and a valid non-negative price.')
      return
    }

    setSaving(true)
    setError(null)
    setNotice(null)

    try {
      const body = JSON.stringify({ name: name.trim(), price: parsedPrice })
      await request<Product>(editingId ? `${productsPath}/${editingId}` : productsPath, {
        method: editingId ? 'PUT' : 'POST',
        body,
      })

      setNotice(editingId ? 'Product updated.' : 'Product created.')
      resetForm()
      await loadProducts(false)
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Unable to save the product.')
    } finally {
      setSaving(false)
    }
  }

  async function deleteProduct(product: Product) {
    if (!window.confirm(`Delete "${product.name}"?`)) {
      return
    }

    setError(null)
    setNotice(null)

    try {
      await request<Product>(`${productsPath}/${product.id}`, { method: 'DELETE' })
      setNotice('Product deleted.')
      if (editingId === product.id) {
        resetForm()
      }
      await loadProducts(false)
    } catch (deleteError) {
      setError(deleteError instanceof Error ? deleteError.message : 'Unable to delete the product.')
    }
  }

  const activeCount = products.filter((product) => !product.isDeleted).length

  return (
    <div className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">NATS JetStream sync lab</p>
          <h1>Backend A</h1>
          <p className="subtitle">
            Write-side product owner. Every change is stored with an outbox event and published to JetStream.
          </p>
        </div>

        <nav className="topbar-actions" aria-label="Related services">
          <a className="ghost-link" href="http://localhost:5174">
            Backend B
          </a>
          <a className="ghost-link" href="http://127.0.0.1:4280" target="_blank" rel="noreferrer">
            Natscope
          </a>
        </nav>
      </header>

      <main className="layout">
        <section className="panel form-panel">
          <div className="section-heading">
            <div>
              <p className="eyebrow">{editingId ? 'Editing product' : 'New product'}</p>
              <h2>{editingId ? 'Update details' : 'Create a product'}</h2>
            </div>
            {editingId && (
              <button className="ghost-button" type="button" onClick={resetForm}>
                Cancel
              </button>
            )}
          </div>

          <form className="product-form" onSubmit={handleSubmit}>
            <label>
              Name
              <input
                value={name}
                onChange={(event) => setName(event.target.value)}
                placeholder="Mechanical keyboard"
                maxLength={200}
                autoComplete="off"
              />
            </label>

            <label>
              Price
              <input
                value={price}
                onChange={(event) => setPrice(event.target.value)}
                placeholder="129.99"
                inputMode="decimal"
                min="0"
                step="0.01"
                type="number"
              />
            </label>

            <button className="primary-button" type="submit" disabled={saving}>
              {saving ? 'Saving...' : editingId ? 'Save changes' : 'Create product'}
            </button>
          </form>

          <p className="hint">
            Successful writes return immediately. Backend B catches up asynchronously through NATS.
          </p>
        </section>

        <section className="panel">
          <div className="section-heading">
            <div>
              <p className="eyebrow">Owner database</p>
              <h2>Products</h2>
            </div>
            <div className="section-meta">
              <span>{activeCount} active</span>
              <button className="ghost-button" type="button" onClick={() => void loadProducts()}>
                Refresh
              </button>
            </div>
          </div>

          {error && (
            <div className="alert alert-error" role="alert">
              {error}
            </div>
          )}

          {notice && (
            <div className="alert alert-success" role="status">
              {notice}
            </div>
          )}

          {loading ? (
            <p className="empty-state">Loading products...</p>
          ) : products.length === 0 ? (
            <p className="empty-state">No products yet. Create one to start the sync flow.</p>
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Product</th>
                    <th>Price</th>
                    <th>Version</th>
                    <th>Updated</th>
                    <th>Status</th>
                    <th />
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
                      <td>
                        <span className={product.isDeleted ? 'status deleted' : 'status active'}>
                          {product.isDeleted ? 'Deleted' : 'Active'}
                        </span>
                      </td>
                      <td>
                        {!product.isDeleted && (
                          <div className="row-actions">
                            <button className="ghost-button" type="button" onClick={() => startEdit(product)}>
                              Edit
                            </button>
                            <button className="danger-button" type="button" onClick={() => void deleteProduct(product)}>
                              Delete
                            </button>
                          </div>
                        )}
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
        <span>Backend A owns the products table and outbox.</span>
        <span>Open Backend B to watch the replica apply each event.</span>
      </footer>
    </div>
  )
}

export default App
