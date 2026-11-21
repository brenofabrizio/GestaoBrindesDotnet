using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Industry> Industries => Set<Industry>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<InventoryStock> Stocks => Set<InventoryStock>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockPosition> StockPositions => Set<StockPosition>();
    public DbSet<StockExitOrder> StockExitOrders => Set<StockExitOrder>();
    public DbSet<SequenceCounter> SequenceCounters => Set<SequenceCounter>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryItem> DeliveryItems => Set<DeliveryItem>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventAllocation> EventAllocations => Set<EventAllocation>();
    public DbSet<InternalRequest> Requests => Set<InternalRequest>();
    public DbSet<InternalRequestItem> RequestItems => Set<InternalRequestItem>();
    public DbSet<InternalRequestStatusHistory> RequestStatusHistory => Set<InternalRequestStatusHistory>();
    public DbSet<ApprovalRule> ApprovalRules => Set<ApprovalRule>();
    public DbSet<RequestApproval> RequestApprovals => Set<RequestApproval>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Category>(entity =>
        {
            entity.HasKey(category => category.Id);
            entity.Property(category => category.Name).HasMaxLength(100).IsRequired();
            entity.Property(category => category.NameKey).HasMaxLength(100).IsRequired();
            entity.Property(category => category.Description).HasMaxLength(255);
            entity.HasIndex(category => category.NameKey).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(category => category.CreatedBy).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(category => category.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Department>(entity =>
        {
            entity.HasKey(department => department.Id);
            entity.Property(department => department.Name).HasMaxLength(100).IsRequired();
            entity.Property(department => department.NameKey).HasMaxLength(100).IsRequired();
            entity.Property(department => department.Description).HasMaxLength(255);
            entity.HasIndex(department => department.NameKey).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(department => department.CreatedBy).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(department => department.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Location>(entity =>
        {
            entity.HasKey(location => location.Id);
            entity.Property(location => location.Name).HasMaxLength(100).IsRequired();
            entity.Property(location => location.NameKey).HasMaxLength(100).IsRequired();
            entity.Property(location => location.Description).HasMaxLength(255);
            entity.Property(location => location.Kind).HasMaxLength(16).IsRequired();
            entity.HasIndex(location => location.NameKey).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(location => location.CreatedBy).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(location => location.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Event>(entity =>
        {
            entity.HasIndex(eventRecord => eventRecord.CreatedBy);
            entity.HasIndex(eventRecord => eventRecord.UpdatedBy);
            entity.HasIndex(eventRecord => eventRecord.OpenedBy);
            entity.HasIndex(eventRecord => eventRecord.ClosedBy);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(eventRecord => eventRecord.CreatedBy).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(eventRecord => eventRecord.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(eventRecord => eventRecord.OpenedBy).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(eventRecord => eventRecord.ClosedBy).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ContactLookup>(entity =>
        {
            entity.UseTpcMappingStrategy();
            entity.HasKey(x => x.Id);
        });
        builder.Entity<Industry>().ToTable("Industries");
        builder.Entity<Supplier>().ToTable("Suppliers");
        ConfigureContactLookup(builder.Entity<Industry>());
        ConfigureContactLookup(builder.Entity<Supplier>());

        builder.Entity<CatalogItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Code).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(150).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(5000);
            entity.Property(item => item.Kind).HasMaxLength(16).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(8).IsRequired();
            entity.Property(item => item.UnitValue).HasPrecision(12, 2);
            entity.Property(item => item.Notes).HasMaxLength(5000);
            entity.Property(item => item.PhotoPath).HasMaxLength(500);
            entity.Property(item => item.ThumbPath).HasMaxLength(500);
            entity.HasIndex(item => item.Code).IsUnique();
            entity.HasIndex(item => item.IndustryId);
            entity.HasIndex(item => item.LocationId);
            entity.HasIndex(item => item.SupplierId);
            entity.HasIndex(item => item.DeletedBy);
            entity.HasOne(item => item.Category).WithMany().HasForeignKey(item => item.CategoryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Industry).WithMany().HasForeignKey(item => item.IndustryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Location).WithMany().HasForeignKey(item => item.LocationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Supplier).WithMany().HasForeignKey(item => item.SupplierId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.DeletedBy).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CreatedBy).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.FullName).HasMaxLength(150).IsRequired();
            entity.Property(user => user.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(user => user.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(user => user.NormalizedEmail).IsUnique().HasDatabaseName("EmailIndex");
            entity.HasIndex(user => user.DeletedAt);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(user => user.DeletedBy).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ApplicationRole>(entity =>
        {
            entity.Property(role => role.Slug).HasMaxLength(40);
            entity.Property(role => role.Description).HasMaxLength(255);
            entity.HasIndex(role => role.Slug).IsUnique();
        });

        builder.Entity<AuthSession>(entity =>
        {
            entity.HasKey(session => session.Id);
            entity.Property(session => session.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(session => session.SecurityStamp).HasMaxLength(256).IsRequired();
            entity.HasIndex(session => session.TokenHash).IsUnique();
            entity.HasIndex(session => new { session.UserId, session.ExpiresAt });
            entity.HasOne(session => session.User)
                .WithMany()
                .HasForeignKey(session => session.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PasswordResetToken>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.UserId, token.ExpiresAt });
            entity.HasOne(token => token.User).WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(log => log.Id);
            entity.ToTable("AuditLogs");
            entity.Property(log => log.UserName).HasMaxLength(150);
            entity.Property(log => log.Action).HasMaxLength(40).IsRequired();
            entity.Property(log => log.EntityType).HasMaxLength(40).IsRequired();
            entity.Property(log => log.EntityLabel).HasMaxLength(190);
            entity.Property(log => log.BeforeJson).HasColumnType("jsonb");
            entity.Property(log => log.AfterJson).HasColumnType("jsonb");
            entity.Property(log => log.Ip).HasMaxLength(45);
            entity.Property(log => log.UserAgent).HasMaxLength(255);
            entity.HasIndex(log => new { log.EntityType, log.EntityId });
            entity.HasIndex(log => new { log.UserId, log.CreatedAt });
            entity.HasIndex(log => log.Action);
            entity.HasIndex(log => log.CreatedAt);
            entity.HasOne(log => log.User).WithMany().HasForeignKey(log => log.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Notification>(entity =>
        {
            entity.HasKey(notification => notification.Id);
            entity.ToTable("Notifications");
            entity.Property(notification => notification.Channel).HasMaxLength(16).IsRequired();
            entity.Property(notification => notification.ToEmail).HasMaxLength(190);
            entity.Property(notification => notification.Subject).HasMaxLength(255).IsRequired();
            entity.Property(notification => notification.Body).HasColumnType("text");
            entity.Property(notification => notification.LinkUrl).HasMaxLength(255);
            entity.Property(notification => notification.RelatedType).HasMaxLength(40);
            entity.Property(notification => notification.Status).HasMaxLength(16).IsRequired();
            entity.Property(notification => notification.LastError).HasMaxLength(500);
            entity.Property(notification => notification.DedupeKey).HasMaxLength(150);
            entity.HasIndex(notification => new { notification.UserId, notification.ReadAt });
            entity.HasIndex(notification => new { notification.Status, notification.Channel, notification.CreatedAt });
            entity.HasIndex(notification => notification.DedupeKey).IsUnique().HasFilter("\"DedupeKey\" IS NOT NULL");
            entity.HasOne(notification => notification.User).WithMany().HasForeignKey(notification => notification.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<InventoryStock>(entity =>
        {
            entity.HasKey(stock => stock.ItemId);
            entity.ToTable("Stock");
            entity.Property(stock => stock.OnHand).HasDefaultValue(0);
            entity.Property(stock => stock.Reserved).HasDefaultValue(0);
            entity.HasOne(stock => stock.Item).WithOne().HasForeignKey<InventoryStock>(stock => stock.ItemId).OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(table => table.HasCheckConstraint("CK_Stock_Balances", "\"OnHand\" >= 0 AND \"Reserved\" >= 0 AND \"Reserved\" <= \"OnHand\""));
        });
        builder.Entity<StockMovement>(entity =>
        {
            entity.HasKey(movement => movement.Id);
            entity.ToTable("StockMovements");
            entity.Property(movement => movement.Type).HasMaxLength(16).IsRequired();
            entity.Property(movement => movement.UnitValue).HasPrecision(12, 2);
            entity.Property(movement => movement.AttachmentPath).HasMaxLength(512);
            entity.Property(movement => movement.Reason).HasMaxLength(255);
            entity.Property(movement => movement.Notes).HasMaxLength(2000);
            entity.Property(movement => movement.DocumentRef).HasMaxLength(255);
            entity.Property(movement => movement.Purpose).HasMaxLength(255);
            entity.Property(movement => movement.Recipient).HasMaxLength(150);
            entity.Property(movement => movement.PurchaseTicketNo).HasMaxLength(50);
            entity.Property(movement => movement.IdempotencyKey).HasMaxLength(80);
            entity.Property(movement => movement.IdempotencyRequestHash).HasMaxLength(64);
            entity.HasIndex(movement => movement.IdempotencyKey).IsUnique();
            entity.HasIndex(movement => new { movement.ItemId, movement.CreatedAt });
            entity.HasOne(movement => movement.Item).WithMany().HasForeignKey(movement => movement.ItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(movement => movement.UserId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Industry>().WithMany().HasForeignKey(movement => movement.IndustryId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(movement => movement.DepartmentId);
            entity.HasOne<Department>().WithMany().HasForeignKey(movement => movement.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(movement => movement.SupplierId);
            entity.HasOne<Supplier>().WithMany().HasForeignKey(movement => movement.SupplierId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Location>().WithMany().HasForeignKey(movement => movement.LocationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Location>().WithMany().HasForeignKey(movement => movement.FromLocationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Location>().WithMany().HasForeignKey(movement => movement.ToLocationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(movement => movement.DeliveryId);
            entity.HasOne<Delivery>().WithMany().HasForeignKey(movement => movement.DeliveryId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<StockPosition>(entity =>
        {
            entity.HasKey(position => new { position.ItemId, position.LocationId });
            entity.ToTable("StockPositions", table => table.HasCheckConstraint("CK_StockPositions_Quantity", "\"Quantity\" >= 0"));
            entity.HasIndex(position => new { position.LocationId, position.ItemId });
            entity.HasOne<CatalogItem>().WithMany().HasForeignKey(position => position.ItemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Location>().WithMany().HasForeignKey(position => position.LocationId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<StockExitOrder>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Code).HasMaxLength(20).IsRequired();
            entity.Property(order => order.Purpose).HasMaxLength(255).IsRequired();
            entity.Property(order => order.Recipient).HasMaxLength(150);
            entity.Property(order => order.PurchaseTicketNo).HasMaxLength(50);
            entity.Property(order => order.DocumentRef).HasMaxLength(60);
            entity.Property(order => order.AttachmentPath).HasMaxLength(512);
            entity.Property(order => order.Notes).HasMaxLength(1000);
            entity.Property(order => order.Status).HasMaxLength(16).IsRequired();
            entity.Property(order => order.IdempotencyKey).HasMaxLength(80);
            entity.Property(order => order.IdempotencyRequestHash).HasMaxLength(64);
            entity.HasIndex(order => order.Code).IsUnique();
            entity.HasIndex(order => new { order.Status, order.CreatedAt });
            entity.HasIndex(order => new { order.AuthorizedBy, order.IdempotencyKey }).IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL");
            entity.HasOne<CatalogItem>().WithMany().HasForeignKey(order => order.ItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Industry>().WithMany().HasForeignKey(order => order.IndustryId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Department>().WithMany().HasForeignKey(order => order.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(order => order.RequesterId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(order => order.AuthorizedBy).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(order => order.ConfirmedBy).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<StockMovement>().WithMany().HasForeignKey(order => order.MovementId).OnDelete(DeleteBehavior.SetNull);
            entity.ToTable(table => table.HasCheckConstraint("CK_StockExitOrders_Quantity", "\"Quantity\" > 0"));
        });

        builder.Entity<SequenceCounter>(entity =>
        {
            entity.ToTable("Sequences");
            entity.HasKey(sequence => new { sequence.Name, sequence.Period });
            entity.Property(sequence => sequence.Name).HasMaxLength(40);
            entity.Property(sequence => sequence.Period).HasMaxLength(10);
            entity.Property(sequence => sequence.LastValue).HasDefaultValue(0);
        });

        builder.Entity<Delivery>(entity =>
        {
            entity.HasKey(delivery => delivery.Id);
            entity.Property(delivery => delivery.Code).HasMaxLength(20).IsRequired();
            entity.Property(delivery => delivery.Type).HasMaxLength(16).IsRequired();
            entity.Property(delivery => delivery.ReceivedByName).HasMaxLength(150).IsRequired();
            entity.Property(delivery => delivery.ReceivedByEmail).HasMaxLength(190);
            entity.Property(delivery => delivery.ReceivedByDocument).HasMaxLength(30);
            entity.Property(delivery => delivery.ReceivedByPhone).HasMaxLength(30);
            entity.Property(delivery => delivery.SignaturePath).HasMaxLength(255).IsRequired();
            entity.Property(delivery => delivery.Notes).HasMaxLength(1000);
            entity.Property(delivery => delivery.IdempotencyKeyHash).HasMaxLength(64).IsRequired();
            entity.Property(delivery => delivery.RequestHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(delivery => delivery.Code).IsUnique();
            entity.HasIndex(delivery => delivery.IdempotencyKeyHash).IsUnique();
            entity.HasIndex(delivery => new { delivery.RequestId, delivery.CreatedAt });
            entity.HasIndex(delivery => new { delivery.EventId, delivery.CreatedAt });
            entity.HasOne(delivery => delivery.Request).WithMany().HasForeignKey(delivery => delivery.RequestId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(delivery => delivery.Event).WithMany().HasForeignKey(delivery => delivery.EventId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Industry>().WithMany().HasForeignKey(delivery => delivery.IndustryId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(delivery => delivery.DeliveredById).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<DeliveryItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.DeliveryId, item.ItemId }).IsUnique();
            entity.Property(item => item.Quantity).IsRequired();
            entity.HasOne(item => item.Delivery).WithMany(delivery => delivery.Items).HasForeignKey(item => item.DeliveryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Item).WithMany().HasForeignKey(item => item.ItemId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("CK_DeliveryItems_Quantity", "\"Quantity\" > 0"));
        });

        builder.Entity<Event>(entity =>
        {
            entity.HasKey(eventRecord => eventRecord.Id);
            entity.Property(eventRecord => eventRecord.Name).HasMaxLength(150).IsRequired();
            entity.Property(eventRecord => eventRecord.Description).HasColumnType("text");
            entity.Property(eventRecord => eventRecord.Venue).HasMaxLength(190);
            entity.Property(eventRecord => eventRecord.StartsOn).HasColumnType("date");
            entity.Property(eventRecord => eventRecord.EndsOn).HasColumnType("date");
            entity.Property(eventRecord => eventRecord.Status).HasMaxLength(20).IsRequired();
            entity.Property(eventRecord => eventRecord.Notes).HasColumnType("text");
            entity.HasIndex(eventRecord => eventRecord.Status);
            entity.HasOne<Location>().WithMany().HasForeignKey(eventRecord => eventRecord.LocationId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<EventAllocation>(entity =>
        {
            entity.HasKey(allocation => allocation.Id);
            entity.Property(allocation => allocation.QuantityAllocated).HasDefaultValue(0).IsRequired();
            entity.Property(allocation => allocation.QuantityWithdrawn).HasDefaultValue(0).IsRequired();
            entity.HasIndex(allocation => new { allocation.EventId, allocation.IndustryId, allocation.ItemId }).IsUnique();
            entity.HasOne<Event>().WithMany().HasForeignKey(allocation => allocation.EventId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Industry>().WithMany().HasForeignKey(allocation => allocation.IndustryId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CatalogItem>().WithMany().HasForeignKey(allocation => allocation.ItemId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_EventAllocations_Quantities",
                "\"QuantityAllocated\" >= 0 AND \"QuantityWithdrawn\" >= 0 AND \"QuantityWithdrawn\" <= \"QuantityAllocated\""));
        });

        builder.Entity<InternalRequest>(entity =>
        {
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Code).HasMaxLength(24).IsRequired();
            entity.Property(request => request.PublicCode).HasMaxLength(40);
            entity.Property(request => request.Flow).HasMaxLength(16).IsRequired();
            entity.Property(request => request.Status).HasMaxLength(32).IsRequired();
            entity.Property(request => request.Purpose).HasMaxLength(255).IsRequired();
            entity.Property(request => request.Recipient).HasMaxLength(150);
            entity.Property(request => request.Notes).HasMaxLength(2000);
            entity.Property(request => request.PurchaseTicketNo).HasMaxLength(50);
            entity.Property(request => request.InvoiceNo).HasMaxLength(60);
            entity.Property(request => request.InvoicePath).HasMaxLength(255);
            entity.Property(request => request.ImportIdentityHash).HasMaxLength(64);
            entity.Property(request => request.ImportContentHash).HasMaxLength(64);
            entity.Property(request => request.CancelReason).HasMaxLength(255);
            entity.Property(request => request.TotalValue).HasPrecision(14, 2);
            entity.HasIndex(request => request.Code).IsUnique();
            entity.HasIndex(request => request.PublicCode).IsUnique();
            entity.HasIndex(request => new { request.Status, request.CreatedAt });
            entity.HasIndex(request => request.RequesterId);
            entity.HasIndex(request => request.IndustryId);
            entity.HasIndex(request => new { request.RequesterId, request.ImportIdentityHash }).IsUnique().HasFilter("\"ImportIdentityHash\" IS NOT NULL");
            entity.HasOne(request => request.Requester).WithMany().HasForeignKey(request => request.RequesterId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(request => request.Department).WithMany().HasForeignKey(request => request.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(request => request.Industry).WithMany().HasForeignKey(request => request.IndustryId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<InternalRequestItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Position).HasDefaultValue(0).IsRequired();
            entity.Property(item => item.UnitValue).HasPrecision(12, 2);
            entity.Property(item => item.Notes).HasMaxLength(255);
            entity.HasIndex(item => new { item.RequestId, item.ItemId }).IsUnique();
            entity.HasIndex(item => new { item.RequestId, item.Position });
            entity.HasOne(item => item.Request).WithMany(request => request.Items).HasForeignKey(item => item.RequestId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Item).WithMany().HasForeignKey(item => item.ItemId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("CK_InternalRequestItems_Quantities", "\"QuantityRequested\" > 0 AND \"QuantityReserved\" >= 0 AND \"QuantityReceived\" >= 0 AND \"QuantityDelivered\" >= 0 AND (\"QuantityApproved\" IS NULL OR \"QuantityApproved\" >= 0)"));
        });

        builder.Entity<InternalRequestStatusHistory>(entity =>
        {
            entity.HasKey(history => history.Id);
            entity.Property(history => history.FromStatus).HasMaxLength(32);
            entity.Property(history => history.ToStatus).HasMaxLength(32).IsRequired();
            entity.Property(history => history.Comment).HasMaxLength(500);
            entity.HasIndex(history => new { history.RequestId, history.CreatedAt });
            entity.HasOne(history => history.Request).WithMany(request => request.History).HasForeignKey(history => history.RequestId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(history => history.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ApprovalRule>(entity =>
        {
            entity.HasKey(rule => rule.Id);
            entity.Property(rule => rule.Name).HasMaxLength(120).IsRequired();
            entity.Property(rule => rule.Criterion).HasMaxLength(16).IsRequired();
            entity.Property(rule => rule.Operator).HasMaxLength(2).IsRequired();
            entity.Property(rule => rule.Value).HasMaxLength(255).IsRequired();
            entity.HasIndex(rule => new { rule.Active, rule.Priority });
            entity.HasOne(rule => rule.ApproverRole).WithMany().HasForeignKey(rule => rule.ApproverRoleId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(rule => rule.ApproverUser).WithMany().HasForeignKey(rule => rule.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RequestApproval>(entity =>
        {
            entity.HasKey(approval => approval.Id);
            entity.Property(approval => approval.Decision).HasMaxLength(16).IsRequired();
            entity.Property(approval => approval.Justification).HasMaxLength(1000);
            entity.HasIndex(approval => new { approval.RequestId, approval.Decision });
            entity.HasOne(approval => approval.Request).WithMany(request => request.Approvals).HasForeignKey(approval => approval.RequestId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(approval => approval.Rule).WithMany().HasForeignKey(approval => approval.RuleId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(approval => approval.ApproverUser).WithMany().HasForeignKey(approval => approval.ApproverUserId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(approval => approval.DecidedBy).WithMany().HasForeignKey(approval => approval.DecidedById).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ApplicationSetting>(entity =>
        {
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(100);
            entity.Property(setting => setting.Value).HasMaxLength(1000).IsRequired();
            entity.HasOne(setting => setting.UpdatedByUser).WithMany().HasForeignKey(setting => setting.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureContactLookup<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity) where TEntity : ContactLookup
    {
        entity.Property(x => x.Name).HasMaxLength(150).IsRequired();
        entity.Property(x => x.NameKey).HasMaxLength(150).IsRequired();
        entity.Property(x => x.Cnpj).HasMaxLength(20);
        entity.Property(x => x.ContactName).HasMaxLength(120);
        entity.Property(x => x.ContactEmail).HasMaxLength(190);
        entity.Property(x => x.ContactPhone).HasMaxLength(30);
        entity.Property(x => x.Notes).HasMaxLength(2000);
        entity.HasIndex(x => x.NameKey).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
        entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
    }
}
