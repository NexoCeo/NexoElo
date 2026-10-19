using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace SaaS.Api.Services;

// Tokens armazenados com AES-GCM e chave separada do banco.
public sealed class WhatsAppStore
{
    private readonly IConfiguration _config;
    public WhatsAppStore(IConfiguration config) => _config = config;
    private NpgsqlConnection Connection() => new(_config.GetConnectionString("DefaultConnection"));
    private byte[] Key()
    {
        var raw = _config["WhatsApp:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("WhatsApp:EncryptionKey ausente.");
        var bytes = Convert.FromBase64String(raw);
        if (bytes.Length != 32) throw new InvalidOperationException("WhatsApp:EncryptionKey exige 32 bytes Base64.");
        return bytes;
    }
    private string Encrypt(string token)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16]; var data = Encoding.UTF8.GetBytes(token);
        var cipher = new byte[data.Length];
        using var aes = new AesGcm(Key(),16);
        aes.Encrypt(nonce,data,cipher,tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }
    public async Task SaveConnection(int userId,string waba,string phone,string token,string? displayNumber,string? displayName)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Token vazio.");
        await using var db = Connection(); await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
INSERT INTO whatsapp_conexoes(usuario_id,waba_id,phone_number_id,token_cifrado,numero,nome_exibicao,status)
VALUES(@user,@waba,@phone,@token,@numero,@nome,'CONECTADO')
ON CONFLICT(usuario_id) DO UPDATE SET
 waba_id=excluded.waba_id,phone_number_id=excluded.phone_number_id,
 token_cifrado=excluded.token_cifrado,numero=excluded.numero,
 nome_exibicao=excluded.nome_exibicao,status='CONECTADO',atualizado_em=now()",db);
        cmd.Parameters.AddWithValue("user",userId);cmd.Parameters.AddWithValue("waba",waba);
        cmd.Parameters.AddWithValue("phone",phone);cmd.Parameters.AddWithValue("token",Encrypt(token));
        cmd.Parameters.AddWithValue("numero",(object?)displayNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("nome",(object?)displayName ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task<object> Status(int userId)
    {
        await using var db=Connection(); await db.OpenAsync();
        await using var cmd=new NpgsqlCommand(@"SELECT numero,nome_exibicao,status,confirmacoes,lembretes,promocoes,horas_antecedencia,limite_mensal_reais FROM whatsapp_conexoes WHERE usuario_id=@id",db);
        cmd.Parameters.AddWithValue("id",userId);
        await using var rd=await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return new {conectado=false,status="NAO_CONFIGURADO",faturamento="META_DIRETO",confirmacoes=false,lembretes=false,promocoes=false,consumoMes=0,mensagensEnviadas=0,mensagensEntregues=0,mensagensFalhas=0,integracaoDisponivel=true};
        return new {conectado=rd.GetString(2)=="CONECTADO",numero=rd.IsDBNull(0)?null:rd.GetString(0),nomeExibicao=rd.IsDBNull(1)?null:rd.GetString(1),status=rd.GetString(2),faturamento="META_DIRETO",
          confirmacoes=rd.GetBoolean(3),lembretes=rd.GetBoolean(4),promocoes=rd.GetBoolean(5),horasAntecedencia=rd.GetInt32(6),limiteMensalReais=rd.GetDecimal(7),
          consumoMes=0,mensagensEnviadas=0,mensagensEntregues=0,mensagensFalhas=0,integracaoDisponivel=true};
    }
    public async Task UpdatePreferences(int id,bool confirmations,bool reminders,bool marketing,int hours,decimal limit)
    {
        await using var db=Connection();await db.OpenAsync();
        await using var cmd=new NpgsqlCommand(@"UPDATE whatsapp_conexoes SET confirmacoes=@c,lembretes=@l,promocoes=@p,horas_antecedencia=@h,limite_mensal_reais=@m,atualizado_em=now() WHERE usuario_id=@id",db);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("c",confirmations);cmd.Parameters.AddWithValue("l",reminders);cmd.Parameters.AddWithValue("p",marketing);cmd.Parameters.AddWithValue("h",hours);cmd.Parameters.AddWithValue("m",limit);
        if(await cmd.ExecuteNonQueryAsync()==0) throw new KeyNotFoundException("Conexao nao encontrada.");
    }
    public async Task Disconnect(int id)
    {
        await using var db=Connection(); await db.OpenAsync();
        await using var cmd=new NpgsqlCommand("DELETE FROM whatsapp_conexoes WHERE usuario_id=@id",db);
        cmd.Parameters.AddWithValue("id",id); await cmd.ExecuteNonQueryAsync();
    }
    public async Task SaveWebhook(string id,string? phone,string? wamid,string type,string? state)
    {
        await using var db=Connection();await db.OpenAsync();
        await using var cmd=new NpgsqlCommand(@"INSERT INTO whatsapp_eventos(identificador,phone_number_id,wamid,tipo,estado) VALUES(@id,@phone,@wamid,@type,@state) ON CONFLICT(identificador) DO NOTHING",db);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("phone",(object?)phone??DBNull.Value);
        cmd.Parameters.AddWithValue("wamid",(object?)wamid??DBNull.Value);cmd.Parameters.AddWithValue("type",type);cmd.Parameters.AddWithValue("state",(object?)state??DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
