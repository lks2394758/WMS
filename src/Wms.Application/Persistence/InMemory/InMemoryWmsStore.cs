using Wms.Domain.Catalog;
using Wms.Domain.Common;
using Wms.Domain.Inventory;
using Wms.Persistence.Abstractions;

namespace Wms.Application.Persistence.InMemory;

/// <summary>
/// Thread-safe in-memory provider for development and tests until the database gate (M4). Data is lost on restart.
/// Write scopes are serialized by one process-wide lock and work on copies until commit, so the provider is atomic
/// and consistent within a single process. It proves nothing about database locking or multi-instance safety
/// (AGENTS.md rule 4).
/// </summary>
public sealed class InMemoryWmsStore
    : ICatalogReader, IInventoryReader, IInventoryStore, IUserStore, IRefreshTokenStore, IAuditLog, ISeedStore, IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);

    private readonly Dictionary<ProductId, Product> _products = new();
    private readonly Dictionary<LocationId, Location> _locations = new();
    private readonly Dictionary<string, Barcode> _barcodes = new(StringComparer.Ordinal);
    private readonly Dictionary<BalanceKey, StoredBalance> _balances = new();
    private readonly Dictionary<(InventoryTransactionType Type, DateOnly Date), int> _sequences = new();
    private readonly List<InventoryTransaction> _transactions = [];
    private readonly List<AuditEntry> _audit = [];
    private readonly Dictionary<(UserId UserId, Guid Key), IdempotencyRecord> _idempotency = new();
    private readonly Dictionary<UserId, UserAccount> _users = new();
    private readonly Dictionary<Guid, RefreshTokenRecord> _refreshTokens = new();
    private readonly Dictionary<string, Guid> _refreshTokenIdsByHash = new(StringComparer.Ordinal);

    /// <summary>Committed ledger, oldest first.</summary>
    public IReadOnlyList<InventoryTransaction> Transactions
    {
        get
        {
            lock (_gate)
            {
                return _transactions.ToArray();
            }
        }
    }

    /// <summary>Committed audit trail, oldest first.</summary>
    public IReadOnlyList<AuditEntry> AuditEntries
    {
        get
        {
            lock (_gate)
            {
                return _audit.ToArray();
            }
        }
    }

    // ---- Seeding (development data and tests). Stock is never seeded: it enters through receipts. ----

    public void AddProduct(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        lock (_gate)
        {
            if (_products.ContainsKey(product.Id)
                || _products.Values.Any(p => string.Equals(p.Sku, product.Sku, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Product {product.Id} or SKU {product.Sku} already exists.");
            }

            _products.Add(product.Id, CopyProduct(product));
        }
    }

    public void AddLocation(Location location)
    {
        ArgumentNullException.ThrowIfNull(location);
        lock (_gate)
        {
            if (_locations.ContainsKey(location.Id)
                || _locations.Values.Any(l => l.WarehouseId == location.WarehouseId && l.Code == location.Code))
            {
                throw new InvalidOperationException($"Location {location.Id} or code {location.Code} already exists.");
            }

            _locations.Add(location.Id, CopyLocation(location));
        }
    }

    public void AddBarcode(Barcode barcode)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        lock (_gate)
        {
            if (!_products.ContainsKey(barcode.ProductId))
            {
                throw new InvalidOperationException($"Product {barcode.ProductId} does not exist.");
            }

            if (!_barcodes.TryAdd(barcode.Value, CopyBarcode(barcode)))
            {
                throw new InvalidOperationException($"Barcode {barcode.Value} already exists.");
            }
        }
    }

    public void AddUser(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            if (_users.ContainsKey(user.Id)
                || _users.Values.Any(u => string.Equals(u.Username, user.Username, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"User {user.Id} or username {user.Username} already exists.");
            }

            _users.Add(user.Id, user);
        }
    }

    // ---- ISeedStore ----

    public Task<int> AddMissingAsync(SeedData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            var inserted = 0;
            foreach (var location in data.Locations.Where(l => !_locations.ContainsKey(l.Id)
                && !_locations.Values.Any(x => x.WarehouseId == l.WarehouseId && x.Code == l.Code)))
            {
                AddLocation(location);
                inserted++;
            }

            foreach (var product in data.Products.Where(p => !_products.ContainsKey(p.Id)
                && !_products.Values.Any(x => string.Equals(x.Sku, p.Sku, StringComparison.OrdinalIgnoreCase))))
            {
                AddProduct(product);
                inserted++;
            }

            foreach (var barcode in data.Barcodes.Where(b => !_barcodes.ContainsKey(b.Value)))
            {
                AddBarcode(barcode);
                inserted++;
            }

            foreach (var user in data.Users.Where(u => !_users.ContainsKey(u.Id)
                && !_users.Values.Any(x => string.Equals(x.Username, u.Username, StringComparison.OrdinalIgnoreCase))))
            {
                AddUser(user);
                inserted++;
            }

            return Task.FromResult(inserted);
        }
    }

    public Task<int> ReplacePasswordHashAsync(
        IReadOnlyCollection<UserId> userIds, string expectedHash, string newHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        lock (_gate)
        {
            var changed = 0;
            foreach (var id in userIds)
            {
                if (_users.TryGetValue(id, out var user) && string.Equals(user.PasswordHash, expectedHash, StringComparison.Ordinal))
                {
                    _users[id] = user with { PasswordHash = newHash };
                    changed++;
                }
            }

            return Task.FromResult(changed);
        }
    }

    // ---- ICatalogReader ----

    public Task<Product?> FindProductAsync(ProductId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<Product?>(_products.TryGetValue(id, out var product) ? CopyProduct(product) : null);
        }
    }

    public Task<PageResult<Product>> ListProductsAsync(PageRequest page, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(Paginate(_products.Values, p => p.Sku, page, CopyProduct));
        }
    }

    public Task<Location?> FindLocationAsync(LocationId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<Location?>(_locations.TryGetValue(id, out var location) ? CopyLocation(location) : null);
        }
    }

    public Task<PageResult<Location>> ListLocationsAsync(PageRequest page, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(Paginate(_locations.Values, l => l.Code, page, CopyLocation));
        }
    }

    public Task<IReadOnlyDictionary<ProductId, IReadOnlyList<string>>> GetActiveBarcodesAsync(
        IReadOnlyCollection<ProductId> productIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        lock (_gate)
        {
            var wanted = productIds.ToHashSet();
            IReadOnlyDictionary<ProductId, IReadOnlyList<string>> result = _barcodes.Values
                .Where(b => b.IsActive && wanted.Contains(b.ProductId))
                .GroupBy(b => b.ProductId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)g.Select(b => b.Value).Order(StringComparer.Ordinal).ToList());
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<Barcode>> FindActiveBarcodesAsync(string value, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<Barcode> result = _barcodes.TryGetValue(value, out var barcode) && barcode.IsActive
                ? [CopyBarcode(barcode)]
                : [];
            return Task.FromResult(result);
        }
    }

    // ---- IInventoryReader ----

    public Task<PageResult<StoredBalance>> ListBalancesAsync(
        BalanceFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        lock (_gate)
        {
            var matching = _balances.Values.Where(b =>
                (filter.ProductId is null || b.Key.ProductId == filter.ProductId)
                && (filter.LocationId is null || b.Key.LocationId == filter.LocationId));
            return Task.FromResult(Paginate(
                matching, b => $"{b.Key.LocationId.Value:N}:{b.Key.ProductId.Value:N}", page, b => b));
        }
    }

    // ---- IInventoryStore ----

    public async Task<IInventoryWriteScope> BeginWriteAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new WriteScope(this);
    }

    // ---- IUserStore ----

    public Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<UserAccount?>(_users.Values.FirstOrDefault(
                u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<UserAccount?> FindByIdAsync(UserId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<UserAccount?>(_users.GetValueOrDefault(id));
        }
    }

    // ---- IRefreshTokenStore ----

    public Task AddAsync(RefreshTokenRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            AddRefreshToken(record);
        }

        return Task.CompletedTask;
    }

    public Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<RefreshTokenRecord?>(
                _refreshTokenIdsByHash.TryGetValue(tokenHash, out var id) ? _refreshTokens.GetValueOrDefault(id) : null);
        }
    }

    public Task<bool> TryRotateAsync(
        Guid currentId, RefreshTokenRecord replacement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (_gate)
        {
            if (!_refreshTokens.TryGetValue(currentId, out var current) || current.RevokedAt is not null)
            {
                return Task.FromResult(false);
            }

            _refreshTokens[currentId] = current with { RevokedAt = now, ReplacedById = replacement.Id };
            AddRefreshToken(replacement);
            return Task.FromResult(true);
        }
    }

    public Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var active = _refreshTokens.Values.Where(t => t.FamilyId == familyId && t.RevokedAt is null).ToList();
            foreach (var token in active)
            {
                _refreshTokens[token.Id] = token with { RevokedAt = now };
            }
        }

        return Task.CompletedTask;
    }

    // ---- IAuditLog ----

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _audit.Add(entry);
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _writer.Dispose();

    private void AddRefreshToken(RefreshTokenRecord record)
    {
        if (!_refreshTokenIdsByHash.TryAdd(record.TokenHash, record.Id))
        {
            throw new InvalidOperationException("Refresh token hash collision.");
        }

        _refreshTokens.Add(record.Id, record);
    }

    private static PageResult<TOut> Paginate<T, TOut>(
        IEnumerable<T> source, Func<T, string> keyOf, PageRequest page, Func<T, TOut> map)
    {
        var window = source
            .Select(item => (Key: keyOf(item), Item: item))
            .Where(x => page.After is null || string.CompareOrdinal(x.Key, page.After) > 0)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Take(page.Limit + 1)
            .ToList();

        var hasMore = window.Count > page.Limit;
        if (hasMore)
        {
            window.RemoveAt(window.Count - 1);
        }

        return new PageResult<TOut>(window.Select(x => map(x.Item)).ToList(), hasMore ? window[^1].Key : null);
    }

    // Stored entities are never handed out: callers get copies, so nothing changes outside a commit.
    private static Product CopyProduct(Product source)
    {
        var copy = Product.Create(source.Id, source.Sku, source.Name, source.Unit, source.Capacity).Value;
        if (!source.IsActive)
        {
            copy.Deactivate();
        }

        return copy;
    }

    private static Location CopyLocation(Location source)
    {
        var copy = Location.Create(source.Id, source.WarehouseId, source.Code).Value;
        if (!source.IsActive)
        {
            copy.Deactivate();
        }

        return copy;
    }

    private static Barcode CopyBarcode(Barcode source)
    {
        var copy = Barcode.Create(source.Value, source.ProductId).Value;
        if (!source.IsActive)
        {
            copy.Deactivate();
        }

        return copy;
    }

    /// <summary>Holds the writer lock from creation until commit or dispose; stages every change until commit.</summary>
    private sealed class WriteScope(InMemoryWmsStore store) : IInventoryWriteScope
    {
        private readonly Dictionary<BalanceKey, InventoryBalance> _locked = new();
        private readonly Dictionary<(InventoryTransactionType Type, DateOnly Date), int> _sequences = new();
        private readonly List<InventoryTransaction> _transactions = [];
        private readonly List<AuditEntry> _audit = [];
        private readonly List<IdempotencyRecord> _idempotency = [];
        private bool _completed;
        private bool _released;

        public Task<IdempotencyRecord?> FindIdempotencyRecordAsync(
            UserId userId, Guid key, DateTimeOffset now, CancellationToken cancellationToken)
        {
            ThrowIfCompleted();
            lock (store._gate)
            {
                return Task.FromResult<IdempotencyRecord?>(
                    store._idempotency.TryGetValue((userId, key), out var record) && record.ExpiresAt > now ? record : null);
            }
        }

        public Task<IReadOnlyDictionary<ProductId, Product>> GetProductsAsync(
            IReadOnlyCollection<ProductId> ids, CancellationToken cancellationToken)
        {
            ThrowIfCompleted();
            lock (store._gate)
            {
                IReadOnlyDictionary<ProductId, Product> result = ids.Distinct()
                    .Where(store._products.ContainsKey)
                    .ToDictionary(id => id, id => CopyProduct(store._products[id]));
                return Task.FromResult(result);
            }
        }

        public Task<IReadOnlyDictionary<LocationId, Location>> GetLocationsAsync(
            IReadOnlyCollection<LocationId> ids, CancellationToken cancellationToken)
        {
            ThrowIfCompleted();
            lock (store._gate)
            {
                IReadOnlyDictionary<LocationId, Location> result = ids.Distinct()
                    .Where(store._locations.ContainsKey)
                    .ToDictionary(id => id, id => CopyLocation(store._locations[id]));
                return Task.FromResult(result);
            }
        }

        public Task<IReadOnlyDictionary<BalanceKey, InventoryBalance>> LockBalancesAsync(
            IReadOnlyCollection<BalanceKey> keys, CancellationToken cancellationToken)
        {
            ThrowIfCompleted();
            lock (store._gate)
            {
                foreach (var key in keys)
                {
                    if (!_locked.ContainsKey(key))
                    {
                        _locked[key] = store._balances.TryGetValue(key, out var stored)
                            ? InventoryBalance.Restore(key.WarehouseId, key.LocationId, key.ProductId, stored.AvailableQuantity)
                            : InventoryBalance.Empty(key.WarehouseId, key.LocationId, key.ProductId);
                    }
                }

                IReadOnlyDictionary<BalanceKey, InventoryBalance> result = keys.Distinct().ToDictionary(k => k, k => _locked[k]);
                return Task.FromResult(result);
            }
        }

        public Task<TransactionNumber> AllocateTransactionNumberAsync(
            InventoryTransactionType type, DateOnly businessDate, CancellationToken cancellationToken)
        {
            ThrowIfCompleted();
            lock (store._gate)
            {
                var slot = (type, businessDate);
                if (!_sequences.TryGetValue(slot, out var last))
                {
                    last = store._sequences.GetValueOrDefault(slot);
                }

                _sequences[slot] = last + 1;
                return Task.FromResult(TransactionNumber.Create(type, businessDate, last + 1));
            }
        }

        public void Add(InventoryTransaction transaction)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            ThrowIfCompleted();
            _transactions.Add(transaction);
        }

        public void Add(AuditEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ThrowIfCompleted();
            _audit.Add(entry);
        }

        public void Add(IdempotencyRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            ThrowIfCompleted();
            _idempotency.Add(record);
        }

        public Task CommitAsync(DateTimeOffset committedAt, CancellationToken cancellationToken)
        {
            ThrowIfCompleted();
            lock (store._gate)
            {
                foreach (var (key, balance) in _locked)
                {
                    store._balances[key] = new StoredBalance(key, balance.AvailableQuantity, committedAt);
                }

                foreach (var (slot, last) in _sequences)
                {
                    store._sequences[slot] = last;
                }

                store._transactions.AddRange(_transactions);
                store._audit.AddRange(_audit);

                var expired = store._idempotency.Where(e => e.Value.ExpiresAt <= committedAt).Select(e => e.Key).ToList();
                foreach (var key in expired)
                {
                    store._idempotency.Remove(key);
                }

                foreach (var record in _idempotency)
                {
                    store._idempotency[(record.UserId, record.Key)] = record;
                }
            }

            _completed = true;
            Release();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _completed = true;
            Release();
            return ValueTask.CompletedTask;
        }

        private void ThrowIfCompleted() =>
            ObjectDisposedException.ThrowIf(_completed, this);

        private void Release()
        {
            if (!_released)
            {
                _released = true;
                store._writer.Release();
            }
        }
    }
}
