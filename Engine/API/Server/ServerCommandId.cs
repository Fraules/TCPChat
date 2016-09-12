﻿namespace Engine.Api.Server
{
  //Команды для сервера: (формат сообщений XX XX Serialized(this.MessageContent))
  //Расшифровка XX XX:
  //8 00 00: Запрос регистрации (в главной комнате)
  //8 00 01: Запрос выхода (из всех комнат)

  //8 00 10: Отправка сообщения всем клиентам в комнате
  //8 00 11: Отправка личного сообщения конкретному юзеру

  //8 00 20: Запрос открытого пароля пользователя

  //8 00 30: Создать комнату
  //8 00 31: Удалить комнату
  //8 00 32: Пригласить пользователей в комнату
  //8 00 33: Кикнуть пользователей из комнаты
  //8 00 34: Выйти из комнаты
  //8 00 35: Запрос обновления комнаты
  //8 00 36: Сделать пользователя администратором комнаты

  //8 00 40: Пинг запрос

  //8 00 50: Добавить файл на раздачу комнаты
  //8 00 51: Удалить файл с раздачи комнаты

  //8 00 60: Запрос прямого соединения
  //8 00 61: Ответ, говорящий о готовности принять входное содеинение

  //8 7F FF: Пустая команда

  enum ServerCommandId : long
  {
    Register = 0x80000,
    Unregister = 0x80001,

    SendRoomMessage = 0x80010,
    SendPrivateMessage = 0x80011,

    GetUserOpenKeyRequest = 0x80020,

    CreateRoom = 0x80030,
    DeleteRoom = 0x80031,
    InvateUsers = 0x80032,
    KickUsers = 0x80033,
    ExitFromRoom = 0x80034,
    RefreshRoom = 0x80035,
    SetRoomAdmin = 0x80036,

    PingRequest = 0x80040,

    AddFileToRoom = 0x80050,
    RemoveFileFromRoom = 0x80051,

    P2PConnectRequest = 0x80060,
    P2PReadyAccept = 0x80061,

    Empty = 0x87FFF
  }
}