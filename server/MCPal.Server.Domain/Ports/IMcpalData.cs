namespace MCPal.Server.Ports;

/// <summary>
/// The application's view of the database: queryable sets, change tracking and transactions. The Storage ring implements it
/// with <c>MCPalDbContext</c>; services and endpoints depend on this port, never on the context.
/// </summary>
internal interface IMcpalData
{
    /// <summary>A tracked query over all rows of <typeparamref name="T"/>. Call <c>AsNoTracking()</c> for read-only use. Every query must filter by company explicitly.</summary>
    IQueryable<T> Query<T>()
        where T : class;

    void Add<T>(T entity)
        where T : class;

    void AddRange<T>(IEnumerable<T> entities)
        where T : class;

    void Remove<T>(T entity)
        where T : class;

    void RemoveRange<T>(IEnumerable<T> entities)
        where T : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task<IDataTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

/// <summary>A database transaction. Disposing without <see cref="CommitAsync"/> rolls back.</summary>
internal interface IDataTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);

    /// <summary>Takes an advisory lock that is held until the transaction ends, so only one instance runs the guarded work at a time.</summary>
    Task LockAsync(long key, CancellationToken cancellationToken);
}
