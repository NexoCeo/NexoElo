using Microsoft.Extensions.Configuration;
using SaaS.Domain.Models;
using SaaS.Domain.Enums;
using SaaS.Application.Interfaces.Repositories;
using Npgsql;

namespace SaaS.Infrastructure.Persistence.Repositories
{
    public class ProfissionalRepository : IProfissionalRepository
    {
        private readonly string _connectionString;

        public ProfissionalRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public async Task<List<UsuarioModel>> ListarEmpresasMesmaCidade(int profissionalId)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
            SELECT u.ID_USUARIO, u.NOME_USUARIO 
            FROM USUARIOS u
            WHERE u.TIPO_USUARIO = @TipoUsuario
              AND u.CIDADE_FK = (
                  SELECT CIDADE_FK FROM USUARIOS WHERE ID_USUARIO = @ProfissionalId
              )";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@TipoUsuario", TipoUsuario.EMPRESA.ToDatabaseValue());
                command.Parameters.AddWithValue("@ProfissionalId", profissionalId);

                var empresas = new List<UsuarioModel>();

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    empresas.Add(new UsuarioModel
                    {
                        Id = reader.GetInt32(reader.GetOrdinal("ID_USUARIO")),
                        Nome = reader.GetString(reader.GetOrdinal("NOME_USUARIO"))
                    });
                }

                return empresas;
            }
            catch
            {
                return null; // ou você pode lançar a exceção e deixar o controller tratar
            }
        }

        public async Task<IEnumerable<EmpresaCidadeModel>> ListarEmpresasPorCidade(int cidadeId)
        {
            var empresas = new List<EmpresaCidadeModel>();
        
            const string sql = @"
                SELECT
                    U.ID_USUARIO,
                    COALESCE(
                        NULLIF(TRIM(E.NOME_FANTASIA), ''),
                        U.NOME_USUARIO
                    ) AS NOME
                FROM USUARIOS U
                LEFT JOIN EMPRESAS E
                    ON E.USUARIO_FK = U.ID_USUARIO
                WHERE U.CIDADE_FK = @CidadeId
                  AND U.TIPO_USUARIO = @TipoUsuario
                ORDER BY NOME;
            ";
        
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
        
            await using var command = new NpgsqlCommand(sql, connection);
        
            command.Parameters.AddWithValue("@CidadeId", cidadeId);
            command.Parameters.AddWithValue(
                "@TipoUsuario",
                TipoUsuario.EMPRESA.ToDatabaseValue()
            );
        
            await using var reader = await command.ExecuteReaderAsync();
        
            while (await reader.ReadAsync())
            {
                empresas.Add(new EmpresaCidadeModel
                {
                    IdUsuario = reader.GetInt32(
                        reader.GetOrdinal("ID_USUARIO")
                    ),
                    Nome = reader.GetString(
                        reader.GetOrdinal("NOME")
                    )
                });
            }
        
            return empresas;
        }

    }
}


