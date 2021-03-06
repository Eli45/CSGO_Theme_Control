﻿//    This file is part of CSGO Theme Control.
//    Copyright (C) 2015  Elijah Furland      
//
//    CSGO Theme Control is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.
//
//    CSGO Theme Control is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//    GNU General Public License for more details.
//
//    You should have received a copy of the GNU General Public License
//    along with CSGO Theme Control.  If not, see <http://www.gnu.org/licenses/>.


namespace CSGO_Theme_Control.Base_Classes.Logger
{
    public static class LoggerSettings
    {
        public enum LogOptions
        {
            DISPLAY_ERROR,  //Note(Eli): This is only used in the event that the log is not being thrown. A thrown log will always display an error and close the app.
            SHOULD_THROW
        }

    }
}
