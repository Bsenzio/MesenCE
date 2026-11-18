#pragma once
#include "pch.h"
#include "Shared/Movies/MesenMovie.h"

//FCEUX movie (.fm2): text format used by most NES TASes published on TASVideos
class Fm2Movie : public MesenMovie
{
private:
	bool ApplySettings(istream& settingsData) override;
	static string ConvertPortData(const string& fm2Buttons);

public:
	using MesenMovie::MesenMovie;

	static bool IsFm2File(vector<uint8_t>& fileData);

	bool Play(VirtualFile& file) override;
	vector<uint8_t> LoadBattery(string extension) override;
};
