using Raffinert.Relations.EntityFrameworkCore;

return typeof(RelationUnitOfWorkMappings).Assembly.GetName().Name ==
    "Raffinert.Relations.EntityFrameworkCore" ? 0 : 1;
