#include "pch.h"
#include "Shared/Movies/Fm2Movie.h"
#include "Shared/Emulator.h"
#include "Shared/NotificationManager.h"
#include "Shared/MessageManager.h"
#include "Shared/BatteryManager.h"
#include "Utilities/StringUtilities.h"
#include "Utilities/VirtualFile.h"
#include "Utilities/ZipReader.h"

//FM2 format: https://fceux.com/web/FM2.html
//Header lines ("key value"), then one line per frame: |commands|port0|port1|port2|
//Gamepad buttons are written in the order "RLDUTSBA" (Right, Left, Down, Up, sTart, Select, B, A),
//a pressed button is any character other than '.' or ' '.
//Commands: bit 0 = soft reset, bit 1 = hard reset (power cycle).

bool Fm2Movie::IsFm2File(vector<uint8_t>& fileData)
{
	return fileData.size() > 10 && memcmp(fileData.data(), "version ", 8) == 0;
}

string Fm2Movie::ConvertPortData(const string& fm2Buttons)
{
	//Mesen's NES controller order is "UDLRSsBA" (Up, Down, Left, Right, Start, select, B, A)
	auto pressed = [&fm2Buttons](size_t index) {
		return index < fm2Buttons.size() && fm2Buttons[index] != '.' && fm2Buttons[index] != ' ';
	};

	static constexpr int fm2Index[8] = { 3, 2, 1, 0, 4, 5, 6, 7 };
	static constexpr char mesenNames[8] = { 'U', 'D', 'L', 'R', 'S', 's', 'B', 'A' };
	string result(8, '.');
	for(int i = 0; i < 8; i++) {
		if(pressed(fm2Index[i])) {
			result[i] = mesenNames[i];
		}
	}
	return result;
}

bool Fm2Movie::Play(VirtualFile& file)
{
	_movieFile = file;

	//No archive (FM2 files are plain text): the base class looks for an optional savestate/battery in it
	_reader.reset(new ZipReader());

	std::stringstream ss;
	file.ReadFile(ss);

	bool port1Connected = true;
	string line;
	while(std::getline(ss, line)) {
		line = StringUtilities::Trim(line);
		if(line.empty()) {
			continue;
		}

		if(line[0] == '|') {
			vector<string> fields = StringUtilities::Split(line.substr(1), '|');
			if(fields.empty()) {
				continue;
			}

			int commands = 0;
			try {
				commands = std::stoi(fields[0]);
			} catch(std::exception&) {
				commands = 0;
			}

			vector<string> frame;
			//System actions device: "RP" (Reset, Power)
			frame.push_back(string() + ((commands & 0x01) ? 'R' : '.') + ((commands & 0x02) ? 'P' : '.'));
			if(fields.size() > 1) {
				frame.push_back(ConvertPortData(fields[1]));
			}
			if(port1Connected && fields.size() > 2) {
				frame.push_back(ConvertPortData(fields[2]));
			}
			_inputData.push_back(frame);
		} else {
			size_t space = line.find(' ');
			string key = space == string::npos ? line : line.substr(0, space);
			string value = space == string::npos ? "" : StringUtilities::Trim(line.substr(space + 1));

			if(key == "binary" && value == "1") {
				MessageManager::Log("[Movie] Binary FM2 movies are not supported.");
				return false;
			} else if(key == "savestate") {
				MessageManager::Log("[Movie] FM2 movies that start from a savestate are not supported.");
				return false;
			} else if(key == "FDS" && value == "1") {
				MessageManager::Log("[Movie] FDS FM2 movies are not supported.");
				return false;
			} else if(key == "port1") {
				port1Connected = value != "0";
			}
		}
	}

	if(_inputData.empty()) {
		MessageManager::Log("[Movie] The FM2 file contains no input data.");
		return false;
	}

	if(_emu->GetConsoleType() != ConsoleType::Nes) {
		MessageManager::DisplayMessage("Movies", "MovieIncorrectConsole", "Nes");
		return false;
	}

	_deviceIndex = 0;

	auto emuLock = _emu->AcquireLock(false);

	//FCEUX movies start from power-on with empty save RAM
	_emu->GetBatteryManager()->SetBatteryProvider(shared_from_this());
	_emu->GetNotificationManager()->RegisterNotificationListener(shared_from_this());
	_emu->PowerCycle();
	_emu->GetBatteryManager()->SetBatteryProvider(nullptr);

	_playing = !_loadFailure;
	return _playing;
}

vector<uint8_t> Fm2Movie::LoadBattery(string extension)
{
	return {};
}

bool Fm2Movie::ApplySettings(istream& settingsData)
{
	return true;
}
