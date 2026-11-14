using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AzureLab.Data
{
    public static class DBErrors
    {
        public static bool IsUniqueViolation(DbUpdateException ex) =>
             ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601); // Unique constraint violation
    }
}
