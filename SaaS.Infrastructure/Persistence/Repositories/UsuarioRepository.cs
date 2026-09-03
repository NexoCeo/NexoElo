using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Identity;
using SaaS.Domain.Models;
using SaaS.Domain.Enums;
using SaaS.Application.Interfaces.Repositories;
using Npgsql;
using NpgsqlTypes;
using System.Globalization;
using System.Text;

namespace SaaS.Infrastructure.Persistence.Repositories
{
    public class UsuarioRepository : IUsuarioRepository
    {
        private readonly string _connectionString;
        private readonly IPasswordHasher<UsuarioModel> _passwordHasher;
        private const string UsuarioSelectBase = @"
            SELECT
                U.ID_USUARIO,
                U.NOME_USUARIO,
                U.EMAIL_USUARIO,
                U.TELEFONE_USUARIO,
                U.SLUG_USUARIO,
                U.SENHA_USUARIO,
                U.FOTO_PERFIL,
                U.TIPO_USUARIO,
                COALESCE(A.STATUS_ASSINATURA, 'NAO_ATIVA') AS ASSINATURA_ATIVA,
                A.DATA_FIM_ASSINATURA,
                U.DATA_CRIACAO_USUARIO,
                U.DATA_ALTERACAO_USUARIO,
                U.CIDADE_FK
            FROM USUARIOS U
            LEFT JOIN ASSINATURAS A ON A.USUARIO_FK = U.ID_USUARIO";

        public UsuarioRepository(
            IConfiguration configuration,
            IPasswordHasher<UsuarioModel> passwordHasher)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string DefaultConnection nao encontrada.");
            _passwordHasher = passwordHasher;
        }


        public Task<UsuarioModel> InserirUsuario(
            UsuarioModel usuarioModel,
            int? empresaProfissionalId = null,
            string statusVinculoProfissional = "APROVADO")
        {
            return InserirUsuario(
                usuarioModel,
                empresaProfissionalId,
                statusVinculoProfissional,
                null);
        }

        public async Task<UsuarioModel> InserirUsuario(
            UsuarioModel usuarioModel,
            int? empresaProfissionalId,
            string statusVinculoProfissional,
            CoordenadasModel? coordenadas)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var emailNormalizado = NormalizarEmail(usuarioModel.Email);
            var telefoneNormalizado = NormalizarTelefone(usuarioModel.Telefone);
            ValidarContatoUsuario(usuarioModel.TipoUsuario, emailNormalizado, telefoneNormalizado);

            var hashedPassword = _passwordHasher.HashPassword(usuarioModel, usuarioModel.Senha);
            usuarioModel.Senha = hashedPassword;
            usuarioModel.Email = emailNormalizado;
            usuarioModel.Telefone = telefoneNormalizado;

            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                usuarioModel.Slug = await GerarSlugUsuario(connection, transaction, usuarioModel);

                var insertUser = new NpgsqlCommand(@"
                INSERT INTO USUARIOS (
                        NOME_USUARIO, EMAIL_USUARIO, TELEFONE_USUARIO, SLUG_USUARIO, SENHA_USUARIO, FOTO_PERFIL, 
                        TIPO_USUARIO, DATA_CRIACAO_USUARIO, 
                        DATA_ALTERACAO_USUARIO, CIDADE_FK
                    )
                    VALUES (
                        @Nome, @Email, @Telefone, @Slug, @Senha, @FotoPerfil, @TipoUsuario,
                        CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, @CidadeFk
                    )
                    RETURNING ID_USUARIO", connection, transaction);

                insertUser.Parameters.AddWithValue("@Nome", usuarioModel.Nome.Trim().ToUpper());
                insertUser.Parameters.AddWithValue("@Email", (object?)emailNormalizado ?? DBNull.Value);
                insertUser.Parameters.AddWithValue("@Telefone", (object?)telefoneNormalizado ?? DBNull.Value);
                insertUser.Parameters.AddWithValue("@Slug", (object?)usuarioModel.Slug ?? DBNull.Value);
                insertUser.Parameters.AddWithValue("@Senha", hashedPassword);
                insertUser.Parameters.AddWithValue("@FotoPerfil", string.IsNullOrEmpty(usuarioModel.FotoPerfil) ? "SEM_FOTO" : usuarioModel.FotoPerfil);
                insertUser.Parameters.AddWithValue("@TipoUsuario", usuarioModel.TipoUsuario.ToDatabaseValue());
                insertUser.Parameters.AddWithValue("@DataCriacao", usuarioModel.DataCriacao);
                insertUser.Parameters.AddWithValue("@DataAlteracao", usuarioModel.DataAlteracao);
                insertUser.Parameters.AddWithValue("@CidadeFk", (object?)usuarioModel.CidadeFk ?? DBNull.Value);

                var id = await insertUser.ExecuteScalarAsync();
                usuarioModel.Id = Convert.ToInt32(id);

                var cmdAssinatura = new NpgsqlCommand(@"
                    INSERT INTO ASSINATURAS (USUARIO_FK, STATUS_ASSINATURA)
                    VALUES (@ID, @StatusAssinatura)", connection, transaction);
                cmdAssinatura.Parameters.AddWithValue("@ID", usuarioModel.Id);
                cmdAssinatura.Parameters.AddWithValue(
                    "@StatusAssinatura",
                    (usuarioModel.AssinaturaAtiva ?? StatusAssinatura.NAO_ATIVA).ToDatabaseValue());
                await cmdAssinatura.ExecuteNonQueryAsync();

                var tipoUsuario = usuarioModel.TipoUsuario;

                if (tipoUsuario == TipoUsuario.CLIENTE)
                {
                    var cmdCliente = new NpgsqlCommand("INSERT INTO CLIENTES (USUARIO_FK) VALUES (@ID)", connection, transaction);
                    cmdCliente.Parameters.AddWithValue("@ID", usuarioModel.Id);
                    await cmdCliente.ExecuteNonQueryAsync();
                }
                else if (tipoUsuario == TipoUsuario.PROFISSIONAL)
                {
                    if (!empresaProfissionalId.HasValue || empresaProfissionalId.Value <= 0)
                        throw new ArgumentException("Informe a empresa responsavel pelo profissional.");

                    await ValidarUsuarioEhTipo(
                        connection,
                        transaction,
                        empresaProfissionalId.Value,
                        TipoUsuario.EMPRESA,
                        "Empresa responsavel nao encontrada.");

                    var cmdProfissional = new NpgsqlCommand("INSERT INTO PROFISSIONAIS (USUARIO_FK) VALUES (@ID)", connection, transaction);
                    cmdProfissional.Parameters.AddWithValue("@ID", usuarioModel.Id);
                    await cmdProfissional.ExecuteNonQueryAsync();

                    var cmdVinculo = new NpgsqlCommand(@"
                        INSERT INTO VINCULOS (
                            PROFISSIONAL_FK,
                            EMPRESA_FK,
                            STATUS_SOLICITACAO
                        )
                        VALUES (
                            @ProfissionalId,
                            @EmpresaId,
                            @StatusSolicitacao
                        )", connection, transaction);

                    cmdVinculo.Parameters.AddWithValue("@ProfissionalId", usuarioModel.Id);
                    cmdVinculo.Parameters.AddWithValue("@EmpresaId", empresaProfissionalId.Value);
                    cmdVinculo.Parameters.AddWithValue("@StatusSolicitacao", statusVinculoProfissional);
                    await cmdVinculo.ExecuteNonQueryAsync();
                }
                else if (tipoUsuario == TipoUsuario.AUTONOMO)
                {
                    var cmdAutonomo = new NpgsqlCommand(@"
                        INSERT INTO AUTONOMOS (USUARIO_FK) 
                        VALUES (@ID)", connection, transaction);
                    cmdAutonomo.Parameters.AddWithValue("@ID", usuarioModel.Id);
                    await cmdAutonomo.ExecuteNonQueryAsync();

                    await InserirPrestadorConfig(connection, transaction, usuarioModel.Id);
                }
                else if (tipoUsuario == TipoUsuario.EMPRESA && usuarioModel is EmpresaModel empresa)
                {
                    if (!string.IsNullOrEmpty(empresa.Cnpj))
                    {
                        empresa.Cnpj = FormatarCNPJ(empresa.Cnpj);
                    }
                    else
                    {
                        empresa.Cnpj = string.IsNullOrEmpty(empresa.Cnpj) ? null : FormatarCNPJ(empresa.Cnpj);
                    }
                    var cmdEmpresa = new NpgsqlCommand(@"
                        INSERT INTO EMPRESAS (USUARIO_FK, NOME_FANTASIA, CNPJ_EMPRESA) 
                        VALUES (@ID, @Fantasia, @CNPJ)", connection, transaction);
                    cmdEmpresa.Parameters.AddWithValue("@ID", usuarioModel.Id);
                    cmdEmpresa.Parameters.AddWithValue("@Fantasia", (object?)empresa.NomeFantasia ?? DBNull.Value);
                    cmdEmpresa.Parameters.AddWithValue("@CNPJ", (object?)empresa.Cnpj ?? DBNull.Value);
                    await cmdEmpresa.ExecuteNonQueryAsync();

                    await InserirPrestadorConfig(connection, transaction, usuarioModel.Id);
                }

                if (coordenadas != null)
                {
                    if (!usuarioModel.CidadeFk.HasValue || usuarioModel.CidadeFk.Value <= 0)
                        throw new InvalidOperationException("Cidade resolvida invalida para salvar a localizacao.");

                    await SalvarLocalizacaoUsuario(
                        connection,
                        transaction,
                        usuarioModel.Id,
                        usuarioModel.CidadeFk.Value,
                        coordenadas);
                }

                await transaction.CommitAsync();
                return usuarioModel;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await transaction.RollbackAsync();
                throw new ArgumentException("Email ou telefone ja esta cadastrado para outro usuario.", ex);
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<IEnumerable<UsuarioModel>> ListarUsuarios()
        {
            var usuariosLista = new List<UsuarioModel>();

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var query = $"{UsuarioSelectBase} ORDER BY U.ID_USUARIO";
                using (var command = new NpgsqlCommand(query, connection))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        usuariosLista.Add(MapUsuario(reader));
                    }
                }
            }

            return usuariosLista;
        }

        public async Task<UsuarioModel?> ListarUsuarioPorId(int usuarioId)
        {
            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var query = $"{UsuarioSelectBase} WHERE U.ID_USUARIO = @UsuarioId";
                using (var command = new NpgsqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@UsuarioId", usuarioId);
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            return MapUsuario(reader);
                        }
                    }
                }
            }

            return null;
        }

        public async Task<UsuarioModel?> ListarUsuarioPorSlug(string slug)
        {
            var slugNormalizado = NormalizarSlug(slug);
            if (slugNormalizado == null)
                return null;

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var query = $@"
                    {UsuarioSelectBase}
                    WHERE U.SLUG_USUARIO = @Slug
                      AND U.TIPO_USUARIO IN ('EMPRESA', 'AUTONOMO')";
                using (var command = new NpgsqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Slug", slugNormalizado);
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            return MapUsuario(reader);
                        }
                    }
                }
            }

            return null;
        }

        public async Task<int> GerarSlugsPendentes()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var usuarios = new List<UsuarioModel>();
                await using (var command = new NpgsqlCommand(@"
                    SELECT
                        U.ID_USUARIO,
                        U.NOME_USUARIO,
                        U.TIPO_USUARIO,
                        E.NOME_FANTASIA
                    FROM USUARIOS U
                    LEFT JOIN EMPRESAS E ON E.USUARIO_FK = U.ID_USUARIO
                    WHERE U.TIPO_USUARIO IN ('EMPRESA', 'AUTONOMO')
                      AND NULLIF(TRIM(COALESCE(U.SLUG_USUARIO, '')), '') IS NULL
                    ORDER BY U.ID_USUARIO", connection, transaction))
                await using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var tipoUsuario = TipoUsuarioExtensions.FromDatabaseValue(reader.GetString(reader.GetOrdinal("TIPO_USUARIO")));
                        var nome = reader.GetString(reader.GetOrdinal("NOME_USUARIO"));
                        var usuarioId = reader.GetInt32(reader.GetOrdinal("ID_USUARIO"));

                        if (tipoUsuario == TipoUsuario.EMPRESA)
                        {
                            usuarios.Add(new EmpresaModel
                            {
                                Id = usuarioId,
                                Nome = nome,
                                TipoUsuario = tipoUsuario,
                                NomeFantasia = reader.IsDBNull(reader.GetOrdinal("NOME_FANTASIA"))
                                    ? null
                                    : reader.GetString(reader.GetOrdinal("NOME_FANTASIA"))
                            });
                        }
                        else
                        {
                            usuarios.Add(new UsuarioModel
                            {
                                Id = usuarioId,
                                Nome = nome,
                                TipoUsuario = tipoUsuario
                            });
                        }
                    }
                }

                var atualizados = 0;
                foreach (var usuario in usuarios)
                {
                    var slug = await GerarSlugUsuario(connection, transaction, usuario);
                    if (slug == null)
                        continue;

                    await using var updateCommand = new NpgsqlCommand(@"
                        UPDATE USUARIOS
                        SET SLUG_USUARIO = @Slug,
                            DATA_ALTERACAO_USUARIO = CURRENT_TIMESTAMP
                        WHERE ID_USUARIO = @Id", connection, transaction);

                    updateCommand.Parameters.AddWithValue("@Slug", slug);
                    updateCommand.Parameters.AddWithValue("@Id", usuario.Id);

                    atualizados += await updateCommand.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
                return atualizados;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public Task<UsuarioModel?> ListarUsuarioPorEmail(string email)
        {
            return ListarUsuarioPorLogin(email);
        }

        public async Task<string?> ObterStatusVinculoProfissionalEmpresa(
            int profissionalId,
            int empresaId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(@"
                SELECT STATUS_SOLICITACAO
                FROM VINCULOS
                WHERE PROFISSIONAL_FK = @ProfissionalId
                  AND EMPRESA_FK = @EmpresaId
                ORDER BY DATA_SOLICITACAO DESC, ID_SOLICITACAO DESC
                LIMIT 1", connection);
            command.Parameters.AddWithValue("@ProfissionalId", profissionalId);
            command.Parameters.AddWithValue("@EmpresaId", empresaId);

            return (await command.ExecuteScalarAsync())?.ToString();
        }

        public async Task<bool> AtualizarAcessoProfissionalEmpresa(
            int profissionalId,
            int empresaId,
            string novaSenha)
        {
            if (string.IsNullOrWhiteSpace(novaSenha))
                throw new ArgumentException("Informe a nova senha do profissional.");

            var senhaHash = _passwordHasher.HashPassword(new UsuarioModel(), novaSenha);
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var vinculoId = await ObterUltimoVinculoId(
                    connection,
                    transaction,
                    profissionalId,
                    empresaId);

                if (!vinculoId.HasValue)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                await using (var usuarioCommand = new NpgsqlCommand(@"
                    UPDATE USUARIOS
                    SET SENHA_USUARIO = @Senha,
                        DATA_ALTERACAO_USUARIO = CURRENT_TIMESTAMP
                    WHERE ID_USUARIO = @ProfissionalId
                      AND TIPO_USUARIO = 'PROFISSIONAL'", connection, transaction))
                {
                    usuarioCommand.Parameters.AddWithValue("@Senha", senhaHash);
                    usuarioCommand.Parameters.AddWithValue("@ProfissionalId", profissionalId);

                    if (await usuarioCommand.ExecuteNonQueryAsync() == 0)
                    {
                        await transaction.RollbackAsync();
                        return false;
                    }
                }

                await AtualizarStatusVinculo(
                    connection,
                    transaction,
                    vinculoId.Value,
                    "APROVADO");
                await transaction.CommitAsync();
                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task RestaurarAcessoProfissionalEmpresa(
            int profissionalId,
            int empresaId,
            string senhaHashAnterior,
            string statusAnterior)
        {
            if (string.IsNullOrWhiteSpace(senhaHashAnterior) ||
                string.IsNullOrWhiteSpace(statusAnterior))
            {
                throw new ArgumentException("Dados anteriores do acesso profissional invalidos.");
            }

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var vinculoId = await ObterUltimoVinculoId(
                    connection,
                    transaction,
                    profissionalId,
                    empresaId);

                if (!vinculoId.HasValue)
                    throw new InvalidOperationException("Vinculo do profissional nao encontrado para restauracao.");

                await using (var usuarioCommand = new NpgsqlCommand(@"
                    UPDATE USUARIOS
                    SET SENHA_USUARIO = @Senha,
                        DATA_ALTERACAO_USUARIO = CURRENT_TIMESTAMP
                    WHERE ID_USUARIO = @ProfissionalId
                      AND TIPO_USUARIO = 'PROFISSIONAL'", connection, transaction))
                {
                    usuarioCommand.Parameters.AddWithValue("@Senha", senhaHashAnterior);
                    usuarioCommand.Parameters.AddWithValue("@ProfissionalId", profissionalId);

                    if (await usuarioCommand.ExecuteNonQueryAsync() == 0)
                        throw new InvalidOperationException("Profissional nao encontrado para restauracao.");
                }

                await AtualizarStatusVinculo(
                    connection,
                    transaction,
                    vinculoId.Value,
                    statusAnterior);
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<UsuarioModel?> ListarUsuarioPorLogin(string login)
        {
            if (string.IsNullOrWhiteSpace(login))
            {
                return null;
            }

            var loginNormalizado = login.Trim();
            var loginEhEmail = loginNormalizado.Contains('@');
            var loginParaBusca = loginEhEmail
                ? NormalizarEmail(loginNormalizado)
                : NormalizarTelefone(loginNormalizado);

            if (loginParaBusca == null)
            {
                return null;
            }

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var query = loginEhEmail
                    ? $@"
                        {UsuarioSelectBase}
                        WHERE UPPER(U.EMAIL_USUARIO) = @Login"
                    : $@"
                        {UsuarioSelectBase}
                        WHERE U.TELEFONE_USUARIO = @Login";
                using (var command = new NpgsqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Login", loginParaBusca);
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            var usuario = MapUsuario(reader);

                            // 🔍 Se for PROFISSIONAL, buscar status de vínculo mais recente
                            if (usuario.TipoUsuario == TipoUsuario.PROFISSIONAL)
                            {
                                await reader.CloseAsync(); // fechar o data reader atual

                                var vinculoQuery = @"
                                    SELECT STATUS_SOLICITACAO 
                                    FROM VINCULOS 
                                    WHERE PROFISSIONAL_FK = @UsuarioId
                                    ORDER BY DATA_SOLICITACAO DESC 
                                    LIMIT 1
                                ";

                                using (var vinculoCmd = new NpgsqlCommand(vinculoQuery, connection))
                                {
                                    vinculoCmd.Parameters.AddWithValue("@UsuarioId", usuario.Id);
                                    using (var vinculoReader = await vinculoCmd.ExecuteReaderAsync())
                                    {
                                        if (await vinculoReader.ReadAsync())
                                        {
                                            usuario.VinculoStatus = vinculoReader.GetString(vinculoReader.GetOrdinal("STATUS_SOLICITACAO"));
                                        }
                                        else
                                        {
                                            usuario.VinculoStatus = "NENHUM_VINCULO"; // default se não houver vínculos
                                        }
                                    }
                                }
                            }

                            return usuario;
                        }
                    }
                }
            }
            return null;
        }

        public async Task<bool> AtualizarDadosUsuario(int usuarioId, string? novoNome, string? novoEmail, string? novaSenha)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var nomeNormalizado = string.IsNullOrWhiteSpace(novoNome) ? null : novoNome.Trim().ToUpper();
            var emailNormalizado = NormalizarEmail(novoEmail);
            var senhaHasheada = string.IsNullOrWhiteSpace(novaSenha)
                ? null
                : _passwordHasher.HashPassword(new UsuarioModel(), novaSenha);

            var query = @"
            UPDATE USUARIOS 
            SET 
                NOME_USUARIO = COALESCE(@Nome, NOME_USUARIO), 
                EMAIL_USUARIO = COALESCE(@Email, EMAIL_USUARIO), 
                SENHA_USUARIO = COALESCE(@Senha, SENHA_USUARIO), 
                DATA_ALTERACAO_USUARIO = CURRENT_TIMESTAMP 
            WHERE ID_USUARIO = @Id";

            await using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@Nome", (object?)nomeNormalizado ?? DBNull.Value);
            command.Parameters.AddWithValue("@Email", (object?)emailNormalizado ?? DBNull.Value);
            command.Parameters.AddWithValue("@Senha", (object?)senhaHasheada ?? DBNull.Value);
            command.Parameters.AddWithValue("@Id", usuarioId);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        public async Task<UsuarioModel?> AtualizarPerfilUsuario(
            int usuarioId,
            string nome,
            string email,
            string? telefone,
            string? fotoPerfil,
            bool atualizarFoto)
        {
            if (usuarioId <= 0)
                throw new ArgumentException("Usuario invalido.");

            var nomeNormalizado = string.IsNullOrWhiteSpace(nome)
                ? throw new ArgumentException("Informe o nome.")
                : nome.Trim().ToUpperInvariant();
            var emailNormalizado = NormalizarEmail(email)
                ?? throw new ArgumentException("Informe o email.");
            var telefoneNormalizado = NormalizarTelefone(telefone);

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                await using var updateCommand = new NpgsqlCommand(@"
                    UPDATE USUARIOS
                    SET NOME_USUARIO = @Nome,
                        EMAIL_USUARIO = @Email,
                        TELEFONE_USUARIO = @Telefone,
                        FOTO_PERFIL = CASE
                            WHEN @AtualizarFoto THEN @FotoPerfil
                            ELSE FOTO_PERFIL
                        END,
                        DATA_ALTERACAO_USUARIO = CURRENT_TIMESTAMP
                    WHERE ID_USUARIO = @UsuarioId", connection, transaction);

                updateCommand.Parameters.AddWithValue("@Nome", nomeNormalizado);
                updateCommand.Parameters.AddWithValue("@Email", emailNormalizado);
                updateCommand.Parameters.AddWithValue(
                    "@Telefone",
                    (object?)telefoneNormalizado ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@AtualizarFoto", atualizarFoto);
                updateCommand.Parameters.AddWithValue(
                    "@FotoPerfil",
                    (object?)fotoPerfil ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@UsuarioId", usuarioId);

                if (await updateCommand.ExecuteNonQueryAsync() == 0)
                {
                    await transaction.RollbackAsync();
                    return null;
                }

                await using (var empresaCommand = new NpgsqlCommand(@"
                    UPDATE EMPRESAS E
                    SET NOME_FANTASIA = @Nome
                    FROM USUARIOS U
                    WHERE E.USUARIO_FK = U.ID_USUARIO
                      AND U.ID_USUARIO = @UsuarioId
                      AND U.TIPO_USUARIO = 'EMPRESA'", connection, transaction))
                {
                    empresaCommand.Parameters.AddWithValue("@Nome", nomeNormalizado);
                    empresaCommand.Parameters.AddWithValue("@UsuarioId", usuarioId);
                    await empresaCommand.ExecuteNonQueryAsync();
                }

                await using var selectCommand = new NpgsqlCommand(
                    $"{UsuarioSelectBase} WHERE U.ID_USUARIO = @UsuarioId",
                    connection,
                    transaction);
                selectCommand.Parameters.AddWithValue("@UsuarioId", usuarioId);

                await using var reader = await selectCommand.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    await transaction.RollbackAsync();
                    return null;
                }

                var usuario = MapUsuario(reader);
                await reader.CloseAsync();
                await transaction.CommitAsync();
                return usuario;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await transaction.RollbackAsync();
                throw new ArgumentException("Email ou telefone ja esta cadastrado para outro usuario.", ex);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task AtualizarSenhaHash(int usuarioId, string senhaHash)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(@"
                UPDATE USUARIOS
                SET SENHA_USUARIO = @Senha,
                    DATA_ALTERACAO_USUARIO = CURRENT_TIMESTAMP
                WHERE ID_USUARIO = @UsuarioId", connection);
            command.Parameters.AddWithValue("@Senha", senhaHash);
            command.Parameters.AddWithValue("@UsuarioId", usuarioId);
            await command.ExecuteNonQueryAsync();
        }


        public string FormatarCNPJ(string cnpj)
        {
            // Remove todos os caracteres não numéricos
            cnpj = new string(cnpj.Where(char.IsDigit).ToArray());

            // Verifica se o CNPJ tem exatamente 14 caracteres
            if (cnpj.Length == 14)
            {
                // Formata o CNPJ
                return string.Format("{0}.{1}.{2}/{3}-{4}",
                    cnpj.Substring(0, 2),
                    cnpj.Substring(2, 3),
                    cnpj.Substring(5, 3),
                    cnpj.Substring(8, 4),
                    cnpj.Substring(12, 2));
            }

            // Se o CNPJ não estiver no formato correto, retorna o próprio valor sem formatação
            return cnpj;
        }

        private static UsuarioModel MapUsuario(NpgsqlDataReader reader)
        {
            return new UsuarioModel
            {
                Id = reader.GetInt32(reader.GetOrdinal("ID_USUARIO")),
                Nome = reader.GetString(reader.GetOrdinal("NOME_USUARIO")),
                Email = reader.IsDBNull(reader.GetOrdinal("EMAIL_USUARIO")) ? null : reader.GetString(reader.GetOrdinal("EMAIL_USUARIO")),
                Telefone = reader.IsDBNull(reader.GetOrdinal("TELEFONE_USUARIO")) ? null : reader.GetString(reader.GetOrdinal("TELEFONE_USUARIO")),
                Slug = reader.IsDBNull(reader.GetOrdinal("SLUG_USUARIO")) ? null : reader.GetString(reader.GetOrdinal("SLUG_USUARIO")),
                Senha = reader.GetString(reader.GetOrdinal("SENHA_USUARIO")),
                FotoPerfil = reader.IsDBNull(reader.GetOrdinal("FOTO_PERFIL")) ? null : reader.GetString(reader.GetOrdinal("FOTO_PERFIL")),
                TipoUsuario = TipoUsuarioExtensions.FromDatabaseValue(reader.GetString(reader.GetOrdinal("TIPO_USUARIO"))),
                AssinaturaAtiva = StatusAssinaturaExtensions.FromDatabaseValue(reader.GetString(reader.GetOrdinal("ASSINATURA_ATIVA"))),
                DataFimAssinatura = reader.IsDBNull(reader.GetOrdinal("DATA_FIM_ASSINATURA")) ? (DateTime?)null : reader.GetDateTime(reader.GetOrdinal("DATA_FIM_ASSINATURA")),
                DataCriacao = reader.GetDateTime(reader.GetOrdinal("DATA_CRIACAO_USUARIO")),
                DataAlteracao = reader.GetDateTime(reader.GetOrdinal("DATA_ALTERACAO_USUARIO")),
                CidadeFk = reader.IsDBNull(reader.GetOrdinal("CIDADE_FK")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("CIDADE_FK"))
            };
        }

        private static string? NormalizarEmail(string? email)
        {
            return string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToUpperInvariant();
        }

        private static string? NormalizarTelefone(string? telefone)
        {
            if (string.IsNullOrWhiteSpace(telefone))
                return null;

            var somenteDigitos = new string(telefone.Where(char.IsDigit).ToArray());
            return string.IsNullOrWhiteSpace(somenteDigitos) ? null : somenteDigitos;
        }

        private static void ValidarContatoUsuario(TipoUsuario tipoUsuario, string? email, string? telefone)
        {
            if (tipoUsuario == TipoUsuario.CLIENTE)
            {
                if (email == null && telefone == null)
                    throw new ArgumentException("Informe email ou telefone para cadastrar o cliente.");

                return;
            }

            if (email == null)
                throw new ArgumentException("Email é obrigatório para esse tipo de usuário.");
        }

        private static async Task<string?> GerarSlugUsuario(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            UsuarioModel usuario)
        {
            if (usuario.TipoUsuario is not (TipoUsuario.EMPRESA or TipoUsuario.AUTONOMO))
                return null;

            var baseSlug = NormalizarSlug(usuario.Slug)
                ?? NormalizarSlug((usuario as EmpresaModel)?.NomeFantasia)
                ?? NormalizarSlug(usuario.Nome)
                ?? $"usuario-{Guid.NewGuid():N}"[..20];

            return await GerarSlugUnico(connection, transaction, baseSlug);
        }

        private static async Task<string> GerarSlugUnico(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string baseSlug)
        {
            var slug = baseSlug;
            var contador = 2;

            while (await SlugExiste(connection, transaction, slug))
            {
                slug = $"{baseSlug}-{contador}";
                contador++;
            }

            return slug;
        }

        private static async Task<bool> SlugExiste(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string slug)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT 1
                FROM USUARIOS
                WHERE SLUG_USUARIO = @Slug
                LIMIT 1", connection, transaction);

            command.Parameters.AddWithValue("@Slug", slug);
            var result = await command.ExecuteScalarAsync();
            return result != null;
        }

        private static async Task<int?> ObterUltimoVinculoId(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int profissionalId,
            int empresaId)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT ID_SOLICITACAO
                FROM VINCULOS
                WHERE PROFISSIONAL_FK = @ProfissionalId
                  AND EMPRESA_FK = @EmpresaId
                ORDER BY DATA_SOLICITACAO DESC, ID_SOLICITACAO DESC
                LIMIT 1
                FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("@ProfissionalId", profissionalId);
            command.Parameters.AddWithValue("@EmpresaId", empresaId);

            var result = await command.ExecuteScalarAsync();
            return result == null || result == DBNull.Value
                ? null
                : Convert.ToInt32(result);
        }

        private static async Task AtualizarStatusVinculo(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int vinculoId,
            string status)
        {
            await using var command = new NpgsqlCommand(@"
                UPDATE VINCULOS
                SET STATUS_SOLICITACAO = @Status
                WHERE ID_SOLICITACAO = @VinculoId", connection, transaction);
            command.Parameters.AddWithValue("@Status", status);
            command.Parameters.AddWithValue("@VinculoId", vinculoId);

            if (await command.ExecuteNonQueryAsync() == 0)
                throw new InvalidOperationException("Nao foi possivel atualizar o vinculo profissional.");
        }

        private static string? NormalizarSlug(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var normalized = value.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();
            var lastWasSeparator = false;

            foreach (var character in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(character);
                if (category == UnicodeCategory.NonSpacingMark)
                    continue;

                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                    lastWasSeparator = false;
                }
                else if (!lastWasSeparator)
                {
                    builder.Append('-');
                    lastWasSeparator = true;
                }
            }

            var slug = builder.ToString().Trim('-');
            return string.IsNullOrWhiteSpace(slug) ? null : slug;
        }

        private static async Task InserirPrestadorConfig(NpgsqlConnection connection, NpgsqlTransaction transaction, int usuarioId)
        {
            var cmdConfig = new NpgsqlCommand(@"
                INSERT INTO PRESTADOR_CONFIG (USUARIO_FK)
                VALUES (@ID)", connection, transaction);
            cmdConfig.Parameters.AddWithValue("@ID", usuarioId);

            await cmdConfig.ExecuteNonQueryAsync();
        }

        private static async Task SalvarLocalizacaoUsuario(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int usuarioId,
            int cidadeId,
            CoordenadasModel coordenadas)
        {
            await using var command = new NpgsqlCommand(@"
                INSERT INTO LOCALIZACAO_USUARIOS (
                    LATITUDE,
                    LONGITUDE,
                    USUARIO_FK,
                    CIDADE_FK)
                VALUES (@Latitude, @Longitude, @UsuarioId, @CidadeId)
                ON CONFLICT (USUARIO_FK)
                DO UPDATE SET
                    LATITUDE = EXCLUDED.LATITUDE,
                    LONGITUDE = EXCLUDED.LONGITUDE,
                    CIDADE_FK = EXCLUDED.CIDADE_FK", connection, transaction);

            command.Parameters.AddWithValue("@Latitude", NpgsqlDbType.Numeric, coordenadas.Latitude);
            command.Parameters.AddWithValue("@Longitude", NpgsqlDbType.Numeric, coordenadas.Longitude);
            command.Parameters.AddWithValue("@UsuarioId", usuarioId);
            command.Parameters.AddWithValue("@CidadeId", cidadeId);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task ValidarUsuarioEhTipo(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int usuarioId,
            TipoUsuario tipoEsperado,
            string mensagemErro)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT TIPO_USUARIO
                FROM USUARIOS
                WHERE ID_USUARIO = @UsuarioId", connection, transaction);

            command.Parameters.AddWithValue("@UsuarioId", usuarioId);

            var result = await command.ExecuteScalarAsync();
            if (result == null)
                throw new ArgumentException(mensagemErro);

            var tipoUsuario = TipoUsuarioExtensions.FromDatabaseValue(result.ToString());
            if (tipoUsuario != tipoEsperado)
                throw new ArgumentException(mensagemErro);
        }
    }
}


